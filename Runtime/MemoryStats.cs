using System;
using System.Collections.Generic;

namespace Framedash
{
	/// <summary>
	/// Source of instantaneous memory readings. Abstracts the engine backend
	/// (UnityEngine.Profiling.Profiler) behind an interface so the omit/emit
	/// logic is unit-testable without an engine. Implementations must NEVER
	/// throw and return false when the underlying value is unavailable
	/// (release player, unsupported platform, or the API itself reporting 0).
	/// </summary>
	public interface IMemoryMetricsSource
	{
		bool TryReadVram(out long vramBytes);
		bool TryReadHeap(out long heapBytes);
	}

	/// <summary>
	/// Metric keys for mem.* readings attached to the metrics map (proto field
	/// 13). Absent = not collected: a source returning false (or a
	/// non-positive reading, which Unity's Profiler APIs use to mean
	/// "unsupported on this platform/build") omits the key entirely rather
	/// than emitting 0, matching the io.* precedent (IoStats/IoHeartbeat).
	/// </summary>
	public static class MemoryStats
	{
		public const string KeyVram = "mem.vram";
		public const string KeyHeap = "mem.heap";
	}

	/// <summary>
	/// Holds the latest memory reading so it can be attached to more than one
	/// event without re-sampling Profiler on every event.
	///
	/// perf_heartbeat is the only event with a fresh read (Refresh is called at
	/// heartbeat cadence, right before the heartbeat's own metrics are built).
	/// But perf_heartbeat carries an empty map_id and no position, so it never
	/// reaches the spatial heatmap grid query (map_id + cell bounds required).
	/// Position-qualified events (Track() calls with a non-empty map id) also
	/// need mem.* so the heatmap has real data -- those attach the CACHED
	/// reading via AppendTo rather than sampling again, keeping the per-event
	/// path Profiler-call-free (allocation/CPU discipline: only cached floats
	/// and precomputed key strings on that path).
	///
	/// Ownership / no-alloc-per-event note: TrackInternal stores the metrics
	/// List reference directly on the TelemetryEvent struct, and EventBuffer /
	/// the offline-persistence path hold that reference until the batch is
	/// actually flushed (serialized) or ack'd -- potentially many heartbeat
	/// cycles later (see IoHeartbeat's per-heartbeat-list-allocation comment,
	/// same lifetime). TelemetrySerializer / PersistenceProvider / BatchPolicy
	/// only ENUMERATE Metrics, never mutate it in place. So Refresh precomputes
	/// one frozen FloatPair list per sample and SWAPS the field reference
	/// (never mutates an already-published list); every position-qualified
	/// event with no caller-supplied metrics between two Refresh calls can then
	/// share that same instance with zero per-event allocation. A later
	/// Refresh publishing a new instance cannot corrupt events that still hold
	/// the earlier one, because the earlier instance is never touched again.
	/// When the caller DID supply a metrics list, AppendTo still appends into
	/// that (already event-owned, already-allocated) list -- unchanged from
	/// before, and permitted by the "per-event when Track() carries
	/// attributes/metrics" allocation-discipline carve-out.
	///
	/// Engine-independent and NUnit-tested; thread-safe (Track() can run off
	/// the main thread while the heartbeat coroutine refreshes on the main
	/// thread).
	/// </summary>
	public sealed class MemoryMetricsCache
	{
		private readonly object _lock = new object();

		// The frozen, shareable snapshot for the "caller supplied no metrics"
		// case. Null when nothing is cached. Vram-first ordering matches the
		// priority used by the cap-aware merge in AppendTo below.
		private List<FloatPair> _cachedList;

		/// <summary>
		/// Re-sample the source and publish a brand-new frozen snapshot list.
		/// Fail-safe: an exception or an unavailable/non-positive reading
		/// clears that half of the cache (the next AppendTo omits the key)
		/// rather than throwing or keeping a stale value from a source that
		/// has since gone bad. Never mutates the PREVIOUS snapshot -- only
		/// swaps the field to a new instance -- so any event already holding a
		/// reference to the old snapshot is unaffected.
		/// </summary>
		public void Refresh(IMemoryMetricsSource source)
		{
			bool hasVram = false;
			float vram = 0f;
			bool hasHeap = false;
			float heap = 0f;

			if (source != null)
			{
				try
				{
					if (source.TryReadVram(out long v) && v > 0L)
					{
						hasVram = true;
						vram = (float)v;
					}
				}
				catch (Exception)
				{
					hasVram = false;
				}

				try
				{
					if (source.TryReadHeap(out long h) && h > 0L)
					{
						hasHeap = true;
						heap = (float)h;
					}
				}
				catch (Exception)
				{
					hasHeap = false;
				}
			}

			List<FloatPair> snapshot = null;
			if (hasVram)
			{
				snapshot = new List<FloatPair>(2) { new FloatPair(MemoryStats.KeyVram, vram) };
			}
			if (hasHeap)
			{
				if (snapshot == null) snapshot = new List<FloatPair>(2);
				snapshot.Add(new FloatPair(MemoryStats.KeyHeap, heap));
			}

			lock (_lock)
			{
				_cachedList = snapshot;
			}
		}

		/// <summary>
		/// Append the cached mem.* readings onto an event's metrics list, or
		/// return it unchanged when nothing is cached.
		///
		/// Null-input (no caller metrics) fast path: returns the cached frozen
		/// snapshot list DIRECTLY, shared across every position-qualified event
		/// until the next Refresh -- zero allocation on the per-event path (see
		/// the class-level ownership comment for why this is safe).
		///
		/// Non-null input: a key already present in <paramref name="metrics"/>
		/// (a caller-supplied metric of the same name) is left alone -- per-
		/// event data always wins on collision, mirroring
		/// SessionManager.MergeAttributes' session-first / event-overrides
		/// order. Also enforces the same FieldClamp.MaxMetrics cap that
		/// FieldClamp.ClampMetrics applies to the caller's dictionary upstream
		/// (Track() runs ClampMetrics before calling AppendTo): a mem.* key is
		/// appended only while the list is still below the cap. A caller who
		/// already supplied MaxMetrics valid entries must see NO behavior
		/// change from this feature (ingest rejects the whole batch on an
		/// over-cap event, so silently pushing a 50-entry list to 51/52 would
		/// be worse than just omitting mem.* for that one event). When only one
		/// slot is left, mem.vram takes priority over mem.heap (VRAM pressure
		/// is the more common heatmap signal for perf regressions).
		/// </summary>
		public List<FloatPair> AppendTo(List<FloatPair> metrics)
		{
			List<FloatPair> cached;
			lock (_lock)
			{
				cached = _cachedList;
			}

			if (cached == null) return metrics;
			if (metrics == null) return cached;

			int count = metrics.Count;
			for (int i = 0; i < cached.Count; i++)
			{
				if (count >= FieldClamp.MaxMetrics) break;
				FloatPair entry = cached[i];
				if (ContainsKey(metrics, entry.Key)) continue;
				metrics.Add(entry);
				count++;
			}

			return metrics;
		}

		private static bool ContainsKey(List<FloatPair> metrics, string key)
		{
			for (int i = 0; i < metrics.Count; i++)
			{
				if (metrics[i].Key == key) return true;
			}
			return false;
		}
	}
}
