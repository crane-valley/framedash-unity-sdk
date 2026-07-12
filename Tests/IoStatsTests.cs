using System.Collections.Generic;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class IoStatsTests
    {
        // A scripted IIoMetricsSource so the auto-source drain/attach path is
        // testable without an engine (AsyncReadManagerMetrics cannot run in the
        // harness). Each Enqueue defines the next window TryReadWindow returns.
        private sealed class FakeIoSource : IIoMetricsSource
        {
            private readonly Queue<(long bytes, float ms, int ops)> _windows =
                new Queue<(long, float, int)>();
            private readonly bool _available;
            public int ReadCount { get; private set; }

            public FakeIoSource(bool available = true) { _available = available; }

            public void Enqueue(long bytes, float ms, int ops)
            {
                _windows.Enqueue((bytes, ms, ops));
            }

            public bool TryReadWindow(out long readBytes, out float readTimeMs, out int readOps)
            {
                readBytes = 0L;
                readTimeMs = 0f;
                readOps = 0;
                ReadCount++;
                if (!_available) return false;
                if (_windows.Count > 0)
                {
                    var w = _windows.Dequeue();
                    readBytes = w.bytes;
                    readTimeMs = w.ms;
                    readOps = w.ops;
                }
                return true;
            }
        }

        // A scripted cumulative counter so IoWindowDelta's delta / re-baseline logic
        // is testable without an engine. Each reading is either a cumulative
        // (bytes, micros, ops) triple or "unavailable". The IoWindowDelta constructor
        // consumes the FIRST reading as its baseline.
        private sealed class FakeCumulativeCounters : ICumulativeIoCounters
        {
            private readonly Queue<(long b, long m, long o)?> _readings =
                new Queue<(long, long, long)?>();

            public void EnqueueReading(long bytes, long micros, long ops)
            {
                _readings.Enqueue((bytes, micros, ops));
            }

            public void EnqueueUnavailable()
            {
                _readings.Enqueue(null);
            }

            public bool TryReadCumulative(out long totalBytes, out long totalReadTimeMicros, out long completedRequests)
            {
                totalBytes = 0L;
                totalReadTimeMicros = 0L;
                completedRequests = 0L;
                if (_readings.Count == 0) return false;
                var r = _readings.Dequeue();
                if (r == null) return false;
                totalBytes = r.Value.b;
                totalReadTimeMicros = r.Value.m;
                completedRequests = r.Value.o;
                return true;
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

        // -- Accumulator drain / reset --

        [Test]
        public void Drain_ReturnsWindowTotals_ThenResets()
        {
            var stats = new IoStats();
            stats.Add(1000L, 5f, 3);
            stats.Add(500L, 2.5f, 1);

            stats.DrainWindow(out long bytes, out float ms, out int ops);
            Assert.That(bytes, Is.EqualTo(1500L));
            Assert.That(ms, Is.EqualTo(7.5f).Within(1e-4));
            Assert.That(ops, Is.EqualTo(4));

            // Second drain is empty: the window reset.
            stats.DrainWindow(out long bytes2, out float ms2, out int ops2);
            Assert.That(bytes2, Is.EqualTo(0L));
            Assert.That(ms2, Is.EqualTo(0f));
            Assert.That(ops2, Is.EqualTo(0));
        }

        // -- Invalid-sample rejection: ANY bad component drops the WHOLE sample and
        //    does NOT latch ever-active (unifies with the Godot SDK semantics) --

        [Test]
        public void Add_FullyInvalidSample_Rejected_DoesNotLatch()
        {
            var stats = new IoStats();
            stats.Add(-100L, float.NaN, -3);
            Assert.That(stats.EverActive, Is.False, "a fully-invalid sample must not activate io.*");
            stats.DrainWindow(out long bytes, out float ms, out int ops);
            Assert.That(bytes, Is.EqualTo(0L));
            Assert.That(ms, Is.EqualTo(0f));
            Assert.That(ops, Is.EqualTo(0));
        }

        [Test]
        public void Add_NegativeBytes_RejectsWholeSample()
        {
            var stats = new IoStats();
            stats.Add(-1L, 5f, 2); // one bad component -> whole sample dropped
            Assert.That(stats.EverActive, Is.False);
            stats.DrainWindow(out long bytes, out float ms, out int ops);
            Assert.That(bytes, Is.EqualTo(0L));
            Assert.That(ms, Is.EqualTo(0f));
            Assert.That(ops, Is.EqualTo(0));
        }

        [Test]
        public void Add_NegativeOps_RejectsWholeSample()
        {
            var stats = new IoStats();
            stats.Add(10L, 5f, -1);
            Assert.That(stats.EverActive, Is.False);
            stats.DrainWindow(out long bytes, out float _, out int _);
            Assert.That(bytes, Is.EqualTo(0L));
        }

        [Test]
        public void Add_NaNReadTime_RejectsWholeSample()
        {
            var stats = new IoStats();
            stats.Add(2048L, float.NaN, 2);
            Assert.That(stats.EverActive, Is.False);
            stats.DrainWindow(out long bytes, out float _, out int ops);
            Assert.That(bytes, Is.EqualTo(0L), "NaN read time drops the whole sample (bytes not kept)");
            Assert.That(ops, Is.EqualTo(0));
        }

        [Test]
        public void Add_InfinityReadTime_RejectsWholeSample()
        {
            var stats = new IoStats();
            stats.Add(10L, float.PositiveInfinity, 1);
            stats.Add(10L, float.NegativeInfinity, 1);
            Assert.That(stats.EverActive, Is.False);
            stats.DrainWindow(out long bytes, out float ms, out int _);
            Assert.That(bytes, Is.EqualTo(0L));
            Assert.That(ms, Is.EqualTo(0f));
        }

        [Test]
        public void Add_NegativeReadTime_RejectsWholeSample()
        {
            var stats = new IoStats();
            stats.Add(10L, -1f, 1);
            Assert.That(stats.EverActive, Is.False);
            stats.DrainWindow(out long bytes, out float _, out int _);
            Assert.That(bytes, Is.EqualTo(0L));
        }

        [Test]
        public void Add_ValidZeroSample_AccumulatesAndLatches()
        {
            var stats = new IoStats();
            stats.Add(0L, 0f, 0); // a live source reporting a quiet window
            Assert.That(stats.EverActive, Is.True, "a valid all-zero sample is a real signal and latches");
            stats.DrainWindow(out long bytes, out float ms, out int ops);
            Assert.That(bytes, Is.EqualTo(0L));
            Assert.That(ms, Is.EqualTo(0f));
            Assert.That(ops, Is.EqualTo(0));
        }

        [Test]
        public void Add_MixedInvalidThenValid_OnlyValidAccumulates()
        {
            var stats = new IoStats();
            stats.Add(-1L, 5f, 2); // dropped in full
            stats.Add(100L, 2f, 3); // kept
            Assert.That(stats.EverActive, Is.True);
            stats.DrainWindow(out long bytes, out float ms, out int ops);
            Assert.That(bytes, Is.EqualTo(100L));
            Assert.That(ms, Is.EqualTo(2f).Within(1e-4));
            Assert.That(ops, Is.EqualTo(3));
        }

        // -- Ever-active gating --

        [Test]
        public void EverActive_FalseBeforeFirstSample_TrueAfter()
        {
            var stats = new IoStats();
            Assert.That(stats.EverActive, Is.False);
            stats.Add(1L, 0f, 0);
            Assert.That(stats.EverActive, Is.True);
        }

        [Test]
        public void EverActive_PreservedAcrossDrains()
        {
            var stats = new IoStats();
            stats.Add(1L, 0f, 0);
            stats.DrainWindow(out long _, out float _, out int _);
            Assert.That(stats.EverActive, Is.True);
        }

        [Test]
        public void BuildMetrics_NullBeforeAnySample()
        {
            var stats = new IoStats();
            Assert.That(IoHeartbeat.BuildMetrics(null, stats), Is.Null);
        }

        [Test]
        public void BuildMetrics_NullStats_ReturnsNull()
        {
            Assert.That(IoHeartbeat.BuildMetrics(null, null), Is.Null);
        }

        [Test]
        public void BuildMetrics_UnavailableAutoSource_DoesNotActivate()
        {
            var stats = new IoStats();
            var source = new FakeIoSource(available: false);
            // Source returns false -> nothing accumulated, never active -> null.
            Assert.That(IoHeartbeat.BuildMetrics(source, stats), Is.Null);
            Assert.That(stats.EverActive, Is.False);
        }

        // -- Manual feed -> heartbeat attach path --

        [Test]
        public void BuildMetrics_ManualFeed_AttachesThreeKeys()
        {
            var stats = new IoStats();
            stats.Add(4096L, 8f, 5); // manual ReportIoSample equivalent

            List<FloatPair> metrics = IoHeartbeat.BuildMetrics(null, stats);
            Assert.That(metrics, Is.Not.Null);
            Assert.That(metrics.Count, Is.EqualTo(3));
            Assert.That(MetricValue(metrics, IoStats.KeyReadBytes), Is.EqualTo(4096f));
            Assert.That(MetricValue(metrics, IoStats.KeyReadTimeMs), Is.EqualTo(8f).Within(1e-4));
            Assert.That(MetricValue(metrics, IoStats.KeyReadOps), Is.EqualTo(5f));
        }

        // -- Automatic source drain path + auto/manual summed in one window --

        [Test]
        public void BuildMetrics_AutoSourceWindow_FoldedIn()
        {
            var stats = new IoStats();
            var source = new FakeIoSource();
            source.Enqueue(1000L, 4f, 2);

            List<FloatPair> metrics = IoHeartbeat.BuildMetrics(source, stats);
            Assert.That(metrics, Is.Not.Null);
            Assert.That(MetricValue(metrics, IoStats.KeyReadBytes), Is.EqualTo(1000f));
            Assert.That(MetricValue(metrics, IoStats.KeyReadTimeMs), Is.EqualTo(4f).Within(1e-4));
            Assert.That(MetricValue(metrics, IoStats.KeyReadOps), Is.EqualTo(2f));
        }

        [Test]
        public void BuildMetrics_AutoPlusManual_SummedInWindow()
        {
            var stats = new IoStats();
            var source = new FakeIoSource();
            source.Enqueue(1000L, 4f, 2);
            stats.Add(500L, 1f, 1); // a manual feed arrived during the window

            List<FloatPair> metrics = IoHeartbeat.BuildMetrics(source, stats);
            Assert.That(MetricValue(metrics, IoStats.KeyReadBytes), Is.EqualTo(1500f));
            Assert.That(MetricValue(metrics, IoStats.KeyReadTimeMs), Is.EqualTo(5f).Within(1e-4));
            Assert.That(MetricValue(metrics, IoStats.KeyReadOps), Is.EqualTo(3f));
        }

        // -- List-reuse safety: successive heartbeats must not share/mutate a list --

        [Test]
        public void BuildMetrics_SuccessiveHeartbeats_ReturnIndependentLists()
        {
            var stats = new IoStats();
            var source = new FakeIoSource();

            source.Enqueue(1000L, 4f, 2);
            List<FloatPair> first = IoHeartbeat.BuildMetrics(source, stats);

            source.Enqueue(7000L, 9f, 8);
            List<FloatPair> second = IoHeartbeat.BuildMetrics(source, stats);

            // Distinct instances so a buffered event carrying `first` is never
            // mutated by the next heartbeat (the pipeline retains the list reference
            // until flush).
            Assert.That(second, Is.Not.SameAs(first));
            // The first list still holds its own (window-reset) values.
            Assert.That(MetricValue(first, IoStats.KeyReadBytes), Is.EqualTo(1000f));
            Assert.That(MetricValue(second, IoStats.KeyReadBytes), Is.EqualTo(7000f));
        }

        // -- IoWindowDelta: non-destructive cumulative -> per-window deltas --

        [Test]
        public void WindowDelta_ComputesDeltaFromCumulative()
        {
            var c = new FakeCumulativeCounters();
            c.EnqueueReading(1000L, 5000L, 3L); // baseline (ctor)
            var d = new IoWindowDelta(c);

            c.EnqueueReading(3000L, 9000L, 7L); // first window
            Assert.That(d.TryReadWindow(out long bytes, out float ms, out int ops), Is.True);
            Assert.That(bytes, Is.EqualTo(2000L));
            Assert.That(ms, Is.EqualTo(4f).Within(1e-4)); // (9000-5000) micros -> 4 ms
            Assert.That(ops, Is.EqualTo(4));
        }

        [Test]
        public void WindowDelta_SuccessiveWindows_AreIncremental()
        {
            var c = new FakeCumulativeCounters();
            c.EnqueueReading(0L, 0L, 0L); // baseline
            var d = new IoWindowDelta(c);

            c.EnqueueReading(100L, 1000L, 1L);
            d.TryReadWindow(out long b1, out float _, out int _);
            Assert.That(b1, Is.EqualTo(100L));

            c.EnqueueReading(250L, 3000L, 4L);
            d.TryReadWindow(out long b2, out float ms2, out int o2);
            Assert.That(b2, Is.EqualTo(150L));
            Assert.That(ms2, Is.EqualTo(2f).Within(1e-4));
            Assert.That(o2, Is.EqualTo(3));
        }

        [Test]
        public void WindowDelta_CounterRegresses_ReBaselinesAndContributesZero()
        {
            var c = new FakeCumulativeCounters();
            c.EnqueueReading(5000L, 8000L, 10L); // baseline
            var d = new IoWindowDelta(c);

            // Host called a destructive clear -> cumulative counters reset lower.
            c.EnqueueReading(2000L, 3000L, 4L);
            Assert.That(d.TryReadWindow(out long b, out float ms, out int ops), Is.True);
            Assert.That(b, Is.EqualTo(0L), "regressed window must contribute nothing");
            Assert.That(ms, Is.EqualTo(0f));
            Assert.That(ops, Is.EqualTo(0));

            // Next window is measured from the NEW baseline (2000/3000/4), not the old one.
            c.EnqueueReading(6000L, 7000L, 9L);
            d.TryReadWindow(out long b2, out float ms2, out int o2);
            Assert.That(b2, Is.EqualTo(4000L));
            Assert.That(ms2, Is.EqualTo(4f).Within(1e-4));
            Assert.That(o2, Is.EqualTo(5));
        }

        [Test]
        public void WindowDelta_PartialRegression_ReBaselines()
        {
            // Only one counter regresses (e.g. an inconsistent host clear): still
            // treated as a reset -> re-baseline, zero window.
            var c = new FakeCumulativeCounters();
            c.EnqueueReading(1000L, 5000L, 5L); // baseline
            var d = new IoWindowDelta(c);

            c.EnqueueReading(2000L, 5000L, 3L); // ops regressed 5 -> 3
            Assert.That(d.TryReadWindow(out long b, out float _, out int ops), Is.True);
            Assert.That(b, Is.EqualTo(0L));
            Assert.That(ops, Is.EqualTo(0));
        }

        [Test]
        public void WindowDelta_Unavailable_ReturnsFalse()
        {
            var c = new FakeCumulativeCounters();
            c.EnqueueUnavailable(); // ctor baseline read fails
            var d = new IoWindowDelta(c);

            c.EnqueueUnavailable(); // window read fails
            Assert.That(d.TryReadWindow(out long _, out float _, out int _), Is.False);
        }

        [Test]
        public void WindowDelta_BaselineDeferred_WhenCtorReadUnavailable()
        {
            var c = new FakeCumulativeCounters();
            c.EnqueueUnavailable(); // ctor could not baseline
            var d = new IoWindowDelta(c);

            c.EnqueueReading(1000L, 2000L, 3L); // first successful read establishes baseline
            Assert.That(d.TryReadWindow(out long b0, out float _, out int _), Is.True);
            Assert.That(b0, Is.EqualTo(0L), "first read after a failed baseline contributes zero");

            c.EnqueueReading(1500L, 2500L, 5L);
            d.TryReadWindow(out long b1, out float ms1, out int o1);
            Assert.That(b1, Is.EqualTo(500L));
            Assert.That(ms1, Is.EqualTo(0.5f).Within(1e-4));
            Assert.That(o1, Is.EqualTo(2));
        }

        [Test]
        public void WindowDelta_ThroughHeartbeat_AttachesDeltaMetrics()
        {
            var c = new FakeCumulativeCounters();
            c.EnqueueReading(0L, 0L, 0L); // baseline
            var d = new IoWindowDelta(c);
            var stats = new IoStats();

            c.EnqueueReading(2048L, 8000L, 4L);
            List<FloatPair> metrics = IoHeartbeat.BuildMetrics(d, stats);
            Assert.That(metrics, Is.Not.Null);
            Assert.That(MetricValue(metrics, IoStats.KeyReadBytes), Is.EqualTo(2048f));
            Assert.That(MetricValue(metrics, IoStats.KeyReadTimeMs), Is.EqualTo(8f).Within(1e-4));
            Assert.That(MetricValue(metrics, IoStats.KeyReadOps), Is.EqualTo(4f));
        }

        [Test]
        public void BuildMetrics_QuietWindowAfterActive_AttachesZeros()
        {
            var stats = new IoStats();
            stats.Add(100L, 1f, 1);
            IoHeartbeat.BuildMetrics(null, stats); // first heartbeat drains it

            // No new samples: still active, so a zero-window heartbeat is emitted
            // (distinguishes "measured, no IO" from "not measured").
            List<FloatPair> quiet = IoHeartbeat.BuildMetrics(null, stats);
            Assert.That(quiet, Is.Not.Null);
            Assert.That(MetricValue(quiet, IoStats.KeyReadBytes), Is.EqualTo(0f));
            Assert.That(MetricValue(quiet, IoStats.KeyReadOps), Is.EqualTo(0f));
        }
    }
}
