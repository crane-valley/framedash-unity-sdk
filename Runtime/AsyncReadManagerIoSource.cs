using System;
#if ENABLE_PROFILER && UNITY_2020_2_OR_NEWER
using Unity.IO.LowLevel.Unsafe;
#endif

namespace Framedash
{
    /// <summary>
    /// Cumulative disk I/O counter backed by Unity's AsyncReadManagerMetrics
    /// (namespace Unity.IO.LowLevel.Unsafe). It counts the engine loader's async
    /// file reads -- AssetBundles, Resources, scene/streaming loads -- so an
    /// asset-load-induced frame-time spike becomes visible in the heartbeat io.*
    /// keys. Per-heartbeat deltas are computed by the engine-independent
    /// IoWindowDelta wrapper; this type only exposes the cumulative totals.
    ///
    /// Availability (verified against Unity docs, see below): the metrics API only
    /// exists in the Editor and DEVELOPMENT builds, and is guarded by the
    /// ENABLE_PROFILER define. A release player compiles this source out entirely
    /// (TryCreate returns null) and falls back to TelemetrySDK.ReportIoSample.
    /// StartCollectingMetrics() must be called first, and every engine call is
    /// wrapped so a platform that lacks the profiler backend silently disables the
    /// source instead of throwing into the game (the SDK NEVER-throw rule).
    ///
    /// Interop caveats (why the read is non-destructive and collection is left on):
    ///   - The metrics buffer is PROCESS-GLOBAL. A host game or a profiling tool may
    ///     also be reading it. So this source reads with Flags.None (cumulative,
    ///     non-destructive) and computes its own deltas, rather than Flags.ClearOnRead
    ///     which would consume samples the host still needs. If the host itself calls
    ///     ClearOnRead, our cumulative counters regress and IoWindowDelta re-baselines
    ///     (that window is under-counted, not garbage).
    ///   - We call StartCollectingMetrics() at TryCreate (idempotent) but NEVER call
    ///     StopCollectingMetrics(): another consumer may have started collection first
    ///     and stopping it would break them. The cost of leaving collection on is a
    ///     small metrics-buffer memory overhead, and only in dev builds / the Editor.
    ///
    /// Docs (Unity 6000.0 / 2023.2 manual "Asset loading metrics" +
    /// Unity.IO.LowLevel.Unsafe.AsyncReadManagerMetrics /
    /// AsyncReadManagerSummaryMetrics scripting reference):
    ///   - Namespace Unity.IO.LowLevel.Unsafe; introduced Unity 2020.2.
    ///   - Guard #if ENABLE_PROFILER && UNITY_2020_2_OR_NEWER; "only available in
    ///     development builds" (Editor also needs -enable-file-read-metrics for full
    ///     coverage; StartCollectingMetrics enables in-script collection).
    ///   - GetCurrentSummaryMetrics(Flags.None) returns the cumulative summary since
    ///     collection start without clearing it.
    ///   - AsyncReadManagerSummaryMetrics fields used: TotalBytesRead,
    ///     NumberOfCompletedRequests, AverageReadTimeMicroseconds (the summary exposes
    ///     an AVERAGE read time, not a total, so cumulative total read time =
    ///     average * completed-request count).
    /// </summary>
    internal sealed class AsyncReadManagerIoSource : ICumulativeIoCounters
    {
#if ENABLE_PROFILER && UNITY_2020_2_OR_NEWER
        /// <summary>
        /// Start metrics collection and return a delta source, or null if the API
        /// throws (for example a platform without the profiler backend). Called once
        /// at init; failure leaves the SDK on the manual feed only.
        /// </summary>
        public static IIoMetricsSource TryCreate()
        {
            try
            {
                // Idempotent; safe if a host already started collection. Never stopped
                // on shutdown (see class comment).
                AsyncReadManagerMetrics.StartCollectingMetrics();
                return new IoWindowDelta(new AsyncReadManagerIoSource());
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool TryReadCumulative(out long totalBytes, out long totalReadTimeMicros, out long completedRequests)
        {
            totalBytes = 0L;
            totalReadTimeMicros = 0L;
            completedRequests = 0L;
            try
            {
                // Flags.None: read the cumulative summary WITHOUT clearing, so a host
                // game / profiler sharing the process-global metrics is not disturbed.
                AsyncReadManagerSummaryMetrics s = AsyncReadManagerMetrics.GetCurrentSummaryMetrics(
                    AsyncReadManagerMetrics.Flags.None);

                totalBytes = (long)s.TotalBytesRead;
                completedRequests = (long)s.NumberOfCompletedRequests;
                // Summary exposes an AVERAGE read time; cumulative total = average * count.
                double totalMicros = (double)s.AverageReadTimeMicroseconds * (double)s.NumberOfCompletedRequests;
                totalReadTimeMicros = totalMicros >= (double)long.MaxValue ? long.MaxValue : (long)totalMicros;
                return true;
            }
            catch (Exception)
            {
                // A mid-run failure must not throw into Update(); disable this tick.
                return false;
            }
        }
#else
        /// <summary>
        /// Release players (and pre-2020.2) have no AsyncReadManagerMetrics: the
        /// source is unavailable and the SDK uses the manual feed only.
        /// </summary>
        public static IIoMetricsSource TryCreate()
        {
            return null;
        }

        public bool TryReadCumulative(out long totalBytes, out long totalReadTimeMicros, out long completedRequests)
        {
            totalBytes = 0L;
            totalReadTimeMicros = 0L;
            completedRequests = 0L;
            return false;
        }
#endif
    }
}
