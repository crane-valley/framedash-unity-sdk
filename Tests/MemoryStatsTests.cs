using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Framedash.Tests
{
	[TestFixture]
	public class MemoryStatsTests
	{
		// A scripted IMemoryMetricsSource so the omit/emit rules are testable
		// without an engine (UnityEngine.Profiling cannot run in the harness).
		private sealed class FakeMemorySource : IMemoryMetricsSource
		{
			public bool VramAvailable;
			public long VramValue;
			public bool HeapAvailable;
			public long HeapValue;
			public bool Throws;

			public bool TryReadVram(out long vramBytes)
			{
				if (Throws) throw new InvalidOperationException("boom");
				vramBytes = VramAvailable ? VramValue : 0L;
				return VramAvailable;
			}

			public bool TryReadHeap(out long heapBytes)
			{
				if (Throws) throw new InvalidOperationException("boom");
				heapBytes = HeapAvailable ? HeapValue : 0L;
				return HeapAvailable;
			}
		}

		private static float MetricValue(List<FloatPair> list, string key)
		{
			foreach (var p in list)
			{
				if (p.Key == key) return p.Value;
			}
			Assert.Fail($"metric key not found: {key}");
			return 0f;
		}

		private static bool HasKey(List<FloatPair> list, string key)
		{
			if (list == null) return false;
			foreach (var p in list)
			{
				if (p.Key == key) return true;
			}
			return false;
		}

		// -- Refresh + AppendTo: the heartbeat path (fresh sample each Refresh) --

		[Test]
		public void AppendTo_NoRefreshYet_ReturnsInputUnchanged()
		{
			var cache = new MemoryMetricsCache();
			Assert.That(cache.AppendTo(null), Is.Null);

			var existing = new List<FloatPair> { new FloatPair(IoStats.KeyReadBytes, 1f) };
			Assert.That(cache.AppendTo(existing), Is.SameAs(existing));
		}

		[Test]
		public void Refresh_NullSource_ClearsCache()
		{
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource { VramAvailable = true, VramValue = 5L });
			cache.Refresh(null);
			Assert.That(cache.AppendTo(null), Is.Null);
		}

		[Test]
		public void Refresh_BothUnavailable_AppendToReturnsNull()
		{
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource { VramAvailable = false, HeapAvailable = false });
			Assert.That(cache.AppendTo(null), Is.Null);
		}

		[Test]
		public void Refresh_VramZero_OmitsKey()
		{
			// Profiler returning 0 means "unsupported" per the API contract, even
			// though TryReadVram in this fake reports "available".
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource { VramAvailable = true, VramValue = 0L, HeapAvailable = false });
			Assert.That(HasKey(cache.AppendTo(null), MemoryStats.KeyVram), Is.False);
		}

		[Test]
		public void Refresh_VramPositive_AppendToEmitsExactKey()
		{
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource { VramAvailable = true, VramValue = 123456789L, HeapAvailable = false });
			var metrics = cache.AppendTo(null);
			Assert.That(metrics, Is.Not.Null);
			Assert.That(metrics.Count, Is.EqualTo(1));
			Assert.That(MetricValue(metrics, "mem.vram"), Is.EqualTo(123456789f));
		}

		[Test]
		public void Refresh_HeapPositive_AppendToEmitsExactKey()
		{
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource { VramAvailable = false, HeapAvailable = true, HeapValue = 987654L });
			var metrics = cache.AppendTo(null);
			Assert.That(metrics, Is.Not.Null);
			Assert.That(metrics.Count, Is.EqualTo(1));
			Assert.That(MetricValue(metrics, "mem.heap"), Is.EqualTo(987654f));
		}

		[Test]
		public void Refresh_BothPositive_AppendToEmitsBothKeys()
		{
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource
			{
				VramAvailable = true, VramValue = 111L,
				HeapAvailable = true, HeapValue = 222L,
			});
			var metrics = cache.AppendTo(null);
			Assert.That(metrics.Count, Is.EqualTo(2));
			Assert.That(MetricValue(metrics, MemoryStats.KeyVram), Is.EqualTo(111f));
			Assert.That(MetricValue(metrics, MemoryStats.KeyHeap), Is.EqualTo(222f));
		}

		[Test]
		public void AppendTo_ReusesExistingIoList()
		{
			var existing = new List<FloatPair> { new FloatPair(IoStats.KeyReadBytes, 42f) };
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource { VramAvailable = true, VramValue = 999L, HeapAvailable = false });

			var metrics = cache.AppendTo(existing);
			Assert.That(metrics, Is.SameAs(existing), "mem.* is appended onto the io.* list, not a separate list");
			Assert.That(metrics.Count, Is.EqualTo(2));
			Assert.That(MetricValue(metrics, IoStats.KeyReadBytes), Is.EqualTo(42f));
			Assert.That(MetricValue(metrics, MemoryStats.KeyVram), Is.EqualTo(999f));
		}

		[Test]
		public void Refresh_SourceThrows_DegradesToAbsent_NeverThrows()
		{
			var cache = new MemoryMetricsCache();
			var source = new FakeMemorySource { Throws = true };
			List<FloatPair> metrics = null;
			Assert.DoesNotThrow(() =>
			{
				cache.Refresh(source);
				metrics = cache.AppendTo(null);
			});
			Assert.That(metrics, Is.Null);
		}

		[Test]
		public void Refresh_PartialThrow_OnlyAffectedKeyOmitted()
		{
			// A source that reports both values successfully must expose both --
			// each half is sampled and validated independently, so a bad reading
			// on one side (see the Throws case above) can never poison the other.
			var cache = new MemoryMetricsCache();
			var source = new FakeMemorySource { HeapAvailable = true, HeapValue = 42L, VramAvailable = true, VramValue = 1L };
			cache.Refresh(source);
			var metrics = cache.AppendTo(null);
			Assert.That(HasKey(metrics, MemoryStats.KeyVram), Is.True);
			Assert.That(HasKey(metrics, MemoryStats.KeyHeap), Is.True);
		}

		// -- Position-qualified (cached, cross-event) attach path --

		[Test]
		public void AppendTo_NullInput_SharesSameInstanceAcrossCalls_NoPerEventAlloc()
		{
			// The whole point of caching (rather than sampling/allocating per
			// event) is that every position-qualified event between two
			// heartbeat refreshes can share ONE frozen snapshot list with zero
			// per-event heap allocation. Assert the SAME reference comes back --
			// this is the P2 fix: previously each null-input call allocated a
			// fresh list, which was a hot-path allocation.
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource { VramAvailable = true, VramValue = 555L, HeapAvailable = false });

			var first = cache.AppendTo(null);
			var second = cache.AppendTo(null);
			var third = cache.AppendTo(null);
			Assert.That(MetricValue(first, MemoryStats.KeyVram), Is.EqualTo(555f));
			Assert.That(second, Is.SameAs(first), "null-input AppendTo must return the same shared snapshot instance");
			Assert.That(third, Is.SameAs(first), "same instance across arbitrarily many calls between refreshes");
		}

		[Test]
		public void Refresh_SwapsInstance_OldSnapshotNeverMutated()
		{
			// Refresh must publish a BRAND-NEW list rather than mutating the
			// previous one in place -- an event that captured a reference to
			// the old snapshot (it may sit in the ring buffer/persistence queue
			// for many heartbeat cycles before flush) must keep reading the
			// value that was live when it was tracked, never a value from a
			// later refresh (no aliasing across snapshots).
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource { VramAvailable = true, VramValue = 100L, HeapAvailable = false });
			var beforeRefresh = cache.AppendTo(null);
			Assert.That(MetricValue(beforeRefresh, MemoryStats.KeyVram), Is.EqualTo(100f));

			cache.Refresh(new FakeMemorySource { VramAvailable = true, VramValue = 200L, HeapAvailable = false });
			var afterRefresh = cache.AppendTo(null);

			// The event still holding `beforeRefresh` must see the OLD value.
			Assert.That(MetricValue(beforeRefresh, MemoryStats.KeyVram), Is.EqualTo(100f),
				"a previously handed-out snapshot must never change value after a later Refresh");
			Assert.That(MetricValue(afterRefresh, MemoryStats.KeyVram), Is.EqualTo(200f));
			Assert.That(afterRefresh, Is.Not.SameAs(beforeRefresh), "Refresh must publish a new instance, not mutate the old one");
		}

		[Test]
		public void AppendTo_CallerSuppliedKey_IsNeverClobbered()
		{
			// A per-event caller metric using the same key name wins on collision --
			// mirrors SessionManager.MergeAttributes' event-overrides-session order.
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource { VramAvailable = true, VramValue = 999L, HeapAvailable = false });

			var callerMetrics = new List<FloatPair> { new FloatPair(MemoryStats.KeyVram, 1.5f) };
			var result = cache.AppendTo(callerMetrics);
			Assert.That(result.Count, Is.EqualTo(1), "the cached value must not be appended alongside the caller's");
			Assert.That(MetricValue(result, MemoryStats.KeyVram), Is.EqualTo(1.5f), "caller-supplied value must survive unchanged");
		}

		[Test]
		public void AppendTo_RefreshClearsPreviouslyCachedValue()
		{
			// A later Refresh (e.g. VRAM support disappears) must not leave a stale
			// cached value visible to subsequent position-qualified events.
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource { VramAvailable = true, VramValue = 1000L, HeapAvailable = false });
			Assert.That(HasKey(cache.AppendTo(null), MemoryStats.KeyVram), Is.True);

			cache.Refresh(new FakeMemorySource { VramAvailable = false, HeapAvailable = false });
			Assert.That(cache.AppendTo(null), Is.Null);
		}

		[Test]
		public void KeyNames_MatchDashboardContract()
		{
			Assert.That(MemoryStats.KeyVram, Is.EqualTo("mem.vram"));
			Assert.That(MemoryStats.KeyHeap, Is.EqualTo("mem.heap"));
		}

		// -- FieldClamp.MaxMetrics cap: a caller already at the ingest limit must
		//    see no behavior change from this feature (P2 fix) --

		private static List<FloatPair> FullCapMetrics(int count)
		{
			var list = new List<FloatPair>(count);
			for (int i = 0; i < count; i++)
			{
				list.Add(new FloatPair($"caller.metric{i}", (float)i));
			}
			return list;
		}

		[Test]
		public void AppendTo_AtCap_AppendsNothing()
		{
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource
			{
				VramAvailable = true, VramValue = 111L,
				HeapAvailable = true, HeapValue = 222L,
			});

			var atCap = FullCapMetrics(FieldClamp.MaxMetrics);
			var result = cache.AppendTo(atCap);

			Assert.That(result, Is.SameAs(atCap));
			Assert.That(result.Count, Is.EqualTo(FieldClamp.MaxMetrics),
				"a caller-supplied MaxMetrics-entry event must be unchanged by this feature");
			Assert.That(HasKey(result, MemoryStats.KeyVram), Is.False);
			Assert.That(HasKey(result, MemoryStats.KeyHeap), Is.False);
		}

		[Test]
		public void AppendTo_OneBelowCap_AppendsExactlyOne_VramFirst()
		{
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource
			{
				VramAvailable = true, VramValue = 111L,
				HeapAvailable = true, HeapValue = 222L,
			});

			var oneBelowCap = FullCapMetrics(FieldClamp.MaxMetrics - 1);
			var result = cache.AppendTo(oneBelowCap);

			Assert.That(result.Count, Is.EqualTo(FieldClamp.MaxMetrics));
			// Priority order when only one slot remains: vram wins over heap.
			Assert.That(HasKey(result, MemoryStats.KeyVram), Is.True);
			Assert.That(HasKey(result, MemoryStats.KeyHeap), Is.False);
		}

		[Test]
		public void AppendTo_TwoBelowCap_AppendsBoth()
		{
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource
			{
				VramAvailable = true, VramValue = 111L,
				HeapAvailable = true, HeapValue = 222L,
			});

			var twoBelowCap = FullCapMetrics(FieldClamp.MaxMetrics - 2);
			var result = cache.AppendTo(twoBelowCap);

			Assert.That(result.Count, Is.EqualTo(FieldClamp.MaxMetrics));
			Assert.That(HasKey(result, MemoryStats.KeyVram), Is.True);
			Assert.That(HasKey(result, MemoryStats.KeyHeap), Is.True);
		}

		[Test]
		public void AppendTo_AtCap_ButKeyAlreadyPresent_LeavesCallerValueUntouched()
		{
			// The caller's own mem.vram counts toward the cap already; AppendTo must
			// not attempt to add a second (duplicate) entry, and heap has no room.
			var cache = new MemoryMetricsCache();
			cache.Refresh(new FakeMemorySource
			{
				VramAvailable = true, VramValue = 999L,
				HeapAvailable = true, HeapValue = 222L,
			});

			var atCap = FullCapMetrics(FieldClamp.MaxMetrics - 1);
			atCap.Add(new FloatPair(MemoryStats.KeyVram, 1.5f));
			var result = cache.AppendTo(atCap);

			Assert.That(result.Count, Is.EqualTo(FieldClamp.MaxMetrics));
			Assert.That(MetricValue(result, MemoryStats.KeyVram), Is.EqualTo(1.5f));
			Assert.That(HasKey(result, MemoryStats.KeyHeap), Is.False);
		}
	}
}
