using System;
using System.Collections.Generic;

namespace Framedash
{
    /// <summary>
    /// Source of disk I/O window samples. Abstracts the engine metrics backend
    /// (Unity's AsyncReadManagerMetrics) behind an interface so the drain/attach
    /// logic is unit-testable without an engine. Implementations must NEVER throw
    /// and must return false when the underlying API is unavailable at runtime.
    /// </summary>
    public interface IIoMetricsSource
    {
        /// <summary>
        /// Read the completed-read totals accumulated since the previous call (a
        /// window delta). Returns false when the backing API is unavailable, in
        /// which case the out values are 0 and the caller treats the source as
        /// "not collected".
        /// </summary>
        bool TryReadWindow(out long readBytes, out float readTimeMs, out int readOps);
    }

    /// <summary>
    /// Thread-safe accumulator for disk I/O window totals. Both the automatic
    /// engine source (drained on each heartbeat) and the manual feed
    /// (TelemetrySDK.ReportIoSample, callable from asset-loading threads) add into
    /// the same window; DrainWindow returns the totals since the previous drain and
    /// resets them to zero. Every component is clamped non-negative finite so a
    /// garbage reading can never produce a value the ingest validator would reject
    /// (which would drop the whole batch). No UnityEngine references -- pure logic,
    /// NUnit-tested. Mirrors the FieldClamp "sanitize before it reaches the wire"
    /// style.
    /// </summary>
    public sealed class IoStats
    {
        // Metric keys attached to the perf_heartbeat metrics map (proto field 13).
        // These are compile-time string literals -> interned, so referencing them
        // per heartbeat allocates nothing.
        public const string KeyReadBytes = "io.read_bytes";
        public const string KeyReadTimeMs = "io.read_time_ms";
        public const string KeyReadOps = "io.read_ops";

        private readonly object _lock = new object();
        private long _readBytes;
        // Accumulated internally as microseconds (long) so the auto + manual feeds
        // sum without float drift; converted back to milliseconds on drain.
        private long _readTimeMicros;
        private long _readOps;
        private bool _everActive;

        /// <summary>
        /// True once ANY source (auto or manual) has fed a sample. Before this is
        /// set the heartbeat attaches no io.* keys at all (absent = not collected;
        /// no 0-stuffing, unlike the fixed perf fields).
        /// </summary>
        public bool EverActive
        {
            get { lock (_lock) return _everActive; }
        }

        /// <summary>
        /// Add a single I/O sample to the current window. The WHOLE sample is
        /// rejected -- no accumulation and no ever-active latch -- if ANY component
        /// is invalid: bytes &lt; 0, ops &lt; 0, or readTimeMs NaN / Infinity /
        /// negative. This matches the Godot SDK: a garbage reading must not silently
        /// floor to zero and permanently activate io.* emission with misleading zero
        /// windows. A VALID all-zero sample is kept and does latch ever-active,
        /// because a live source reporting a genuinely quiet window is a real signal.
        /// </summary>
        public void Add(long readBytes, float readTimeMs, int readOps)
        {
            if (readBytes < 0L || readOps < 0) return;
            if (float.IsNaN(readTimeMs) || float.IsInfinity(readTimeMs) || readTimeMs < 0f) return;

            long micros = 0L;
            if (readTimeMs > 0f)
            {
                double m = (double)readTimeMs * 1000.0;
                micros = m >= (double)long.MaxValue ? long.MaxValue : (long)m;
            }

            lock (_lock)
            {
                _everActive = true;
                _readBytes = SafeAdd(_readBytes, readBytes);
                _readTimeMicros = SafeAdd(_readTimeMicros, micros);
                _readOps = SafeAdd(_readOps, (long)readOps);
            }
        }

        /// <summary>
        /// Return the window totals since the previous drain and reset the window
        /// to zero. EverActive is preserved across drains. The returned values are
        /// finite and non-negative, ready to attach to the heartbeat metrics.
        /// </summary>
        public void DrainWindow(out long readBytes, out float readTimeMs, out int readOps)
        {
            lock (_lock)
            {
                readBytes = _readBytes;
                readTimeMs = (float)(_readTimeMicros / 1000.0);
                readOps = _readOps > int.MaxValue ? int.MaxValue : (int)_readOps;
                _readBytes = 0L;
                _readTimeMicros = 0L;
                _readOps = 0L;
            }
        }

        // Saturating add: both inputs are already non-negative, so a wrap to a
        // negative value can only come from overflow -> clamp to long.MaxValue
        // rather than letting a negative escape to the wire.
        private static long SafeAdd(long a, long b)
        {
            long sum = a + b;
            if (sum < 0L) return long.MaxValue;
            return sum;
        }
    }

    /// <summary>
    /// Cumulative disk I/O counters since collection start: a monotonic,
    /// NON-DESTRUCTIVE read. Abstracts the engine backend so the delta / re-baseline
    /// logic (IoWindowDelta) is unit-testable without an engine. Implementations must
    /// NEVER throw and return false when the backend is unavailable. Reading
    /// cumulatively (rather than a destructive clear) is deliberate: the underlying
    /// engine metrics are process-global and may be shared with the host game or a
    /// profiling tool, so the SDK must not consume samples out from under them.
    /// </summary>
    public interface ICumulativeIoCounters
    {
        bool TryReadCumulative(out long totalBytes, out long totalReadTimeMicros, out long completedRequests);
    }

    /// <summary>
    /// Turns cumulative, non-destructive I/O counters into per-heartbeat window
    /// deltas. Keeps the last-seen cumulative snapshot (bytes, derived total read
    /// time, completed-request count) and subtracts it each window. If ANY counter
    /// regresses -- the host called a destructive clear, or collection restarted --
    /// it re-baselines from the current reading and contributes nothing for that
    /// window rather than emitting a garbage negative delta. Marks the source
    /// available (returns true) even for a zero / re-baselined window, so a live dev
    /// build is treated as an active source. Engine-independent, NUnit-tested.
    /// </summary>
    public sealed class IoWindowDelta : IIoMetricsSource
    {
        private readonly ICumulativeIoCounters _counters;
        private long _lastBytes;
        private long _lastMicros;
        private long _lastOps;
        private bool _hasBaseline;

        public IoWindowDelta(ICumulativeIoCounters counters)
        {
            _counters = counters;
            // Baseline immediately so the first heartbeat captures the window since
            // construction (init), not since the first heartbeat. If the backend is
            // unavailable here, the baseline stays unset and the first successful
            // read establishes it.
            if (_counters != null
                && _counters.TryReadCumulative(out long b, out long m, out long o))
            {
                _lastBytes = b;
                _lastMicros = m;
                _lastOps = o;
                _hasBaseline = true;
            }
        }

        public bool TryReadWindow(out long readBytes, out float readTimeMs, out int readOps)
        {
            readBytes = 0L;
            readTimeMs = 0f;
            readOps = 0;

            if (_counters == null
                || !_counters.TryReadCumulative(out long curBytes, out long curMicros, out long curOps))
            {
                return false;
            }

            if (!_hasBaseline
                || curBytes < _lastBytes || curMicros < _lastMicros || curOps < _lastOps)
            {
                // First reading, or a counter went backwards (host clear / restart):
                // re-baseline and contribute nothing for this window. The source is
                // still available (true), so a quiet/re-baselined window is emitted
                // as zeros rather than dropping the source.
                _lastBytes = curBytes;
                _lastMicros = curMicros;
                _lastOps = curOps;
                _hasBaseline = true;
                return true;
            }

            long dMicros = curMicros - _lastMicros;
            long dOps = curOps - _lastOps;
            readBytes = curBytes - _lastBytes;
            readTimeMs = (float)(dMicros / 1000.0);
            readOps = dOps > int.MaxValue ? int.MaxValue : (int)dOps;

            _lastBytes = curBytes;
            _lastMicros = curMicros;
            _lastOps = curOps;
            return true;
        }
    }

    /// <summary>
    /// Builds the io.* metrics list attached to a perf_heartbeat. Pulls the window
    /// delta from the optional automatic source into the shared accumulator, then
    /// drains the accumulator. Returns null until a source has ever been active
    /// (absent = not collected); otherwise a freshly allocated 3-entry list.
    /// Engine-independent, NUnit-tested.
    /// </summary>
    public static class IoHeartbeat
    {
        /// <summary>
        /// Pull + drain the window and build the heartbeat metrics list, or null.
        /// </summary>
        public static List<FloatPair> BuildMetrics(IIoMetricsSource autoSource, IoStats stats)
        {
            if (stats == null) return null;

            // Fold the automatic source's window delta into the shared accumulator
            // so it sums with any manual ReportIoSample feeds. TryReadWindow returns
            // false when the API is unavailable (release player); we then rely on
            // the manual feed alone.
            if (autoSource != null
                && autoSource.TryReadWindow(out long autoBytes, out float autoMs, out int autoOps))
            {
                stats.Add(autoBytes, autoMs, autoOps);
            }

            if (!stats.EverActive) return null;

            stats.DrainWindow(out long bytes, out float timeMs, out int ops);

            // A FRESH list per heartbeat, deliberately not reused: the event pipeline
            // retains the metrics-list reference inside the ring buffer until the next
            // flush (EventBuffer holds the struct by value but the List by reference,
            // released only in DequeueAll), and the number of heartbeats buffered
            // before a flush is bounded only by the batch/buffer size (the 100-event
            // and payload-size flush triggers, plus a caller-configurable flush
            // interval) and by the persist-failure re-enqueue path -- NOT by a small
            // constant. So neither a double-buffer nor any fixed-size reuse pool is
            // provably safe here. Allocating one 3-entry list every ~10s is within the
            // SDK allocation discipline (per-event metric allocation is explicitly
            // allowed; only the per-frame loop must be alloc-free), while the true hot
            // path -- per-frame IO accumulation via IoStats.Add -- allocates nothing.
            var list = new List<FloatPair>(3);
            list.Add(new FloatPair(IoStats.KeyReadBytes, (float)bytes));
            list.Add(new FloatPair(IoStats.KeyReadTimeMs, timeMs));
            list.Add(new FloatPair(IoStats.KeyReadOps, (float)ops));
            return list;
        }
    }
}
