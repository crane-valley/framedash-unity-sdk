using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class MapLoadTimerTests
    {
        // -- Constants (cross-SDK event / metric contract) --

        [Test]
        public void EventNameAndMetricKey_MatchWireContract()
        {
            Assert.That(MapLoadTimer.MapLoadEventName, Is.EqualTo("map_load"));
            Assert.That(MapLoadTimer.KeyLoadTimeMs, Is.EqualTo("load_time_ms"));
            // The map name rides the attributes map (map_id stays empty so the event is
            // not treated as spatial or activation-qualifying).
            Assert.That(MapLoadTimer.KeyMapName, Is.EqualTo("map_name"));
        }

        // -- Begin / End timing --

        [Test]
        public void End_AfterBegin_ComputesElapsedMs()
        {
            var timer = new MapLoadTimer();
            timer.Begin("world_1", 10.0);

            bool completed = timer.End(12.5, out string mapName, out double elapsedMs);

            Assert.That(completed, Is.True);
            Assert.That(mapName, Is.EqualTo("world_1"));
            Assert.That(elapsedMs, Is.EqualTo(2500.0).Within(1e-6));
        }

        [Test]
        public void End_SubSecondLoad_ComputesFractionalMs()
        {
            var timer = new MapLoadTimer();
            timer.Begin("boot", 100.0);

            timer.End(100.25, out _, out double elapsedMs);

            Assert.That(elapsedMs, Is.EqualTo(250.0).Within(1e-6));
        }

        // -- End without Begin is a no-op --

        [Test]
        public void End_WithoutBegin_ReturnsFalseNoOp()
        {
            var timer = new MapLoadTimer();

            bool completed = timer.End(5.0, out string mapName, out double elapsedMs);

            Assert.That(completed, Is.False);
            Assert.That(mapName, Is.EqualTo(""));
            Assert.That(elapsedMs, Is.EqualTo(0.0));
        }

        [Test]
        public void End_Twice_SecondReturnsFalse()
        {
            var timer = new MapLoadTimer();
            timer.Begin("m", 1.0);

            Assert.That(timer.End(2.0, out _, out _), Is.True);
            // Pending state cleared after the first End.
            Assert.That(timer.End(3.0, out _, out double elapsedMs), Is.False);
            Assert.That(elapsedMs, Is.EqualTo(0.0));
        }

        // -- Begin again before End replaces the pending measurement --

        [Test]
        public void Begin_Twice_ReplacesPendingMeasurement()
        {
            var timer = new MapLoadTimer();
            timer.Begin("first", 10.0);
            timer.Begin("second", 20.0);

            bool completed = timer.End(21.0, out string mapName, out double elapsedMs);

            Assert.That(completed, Is.True);
            // The second Begin wins: map name and start timestamp are the later pair.
            Assert.That(mapName, Is.EqualTo("second"));
            Assert.That(elapsedMs, Is.EqualTo(1000.0).Within(1e-6));
        }

        // -- Backwards clock is floored at 0, never negative --

        [Test]
        public void End_BackwardsClock_FloorsElapsedAtZero()
        {
            var timer = new MapLoadTimer();
            timer.Begin("m", 50.0);

            timer.End(49.0, out _, out double elapsedMs);

            Assert.That(elapsedMs, Is.EqualTo(0.0));
        }

        [Test]
        public void Begin_NullMapName_StoredAsEmpty()
        {
            var timer = new MapLoadTimer();
            timer.Begin(null, 0.0);

            timer.End(1.0, out string mapName, out _);

            Assert.That(mapName, Is.EqualTo(""));
        }

        // -- HasPending reflects state --

        [Test]
        public void HasPending_TracksBeginEndLifecycle()
        {
            var timer = new MapLoadTimer();
            Assert.That(timer.HasPending, Is.False);

            timer.Begin("m", 0.0);
            Assert.That(timer.HasPending, Is.True);

            timer.End(1.0, out _, out _);
            Assert.That(timer.HasPending, Is.False);
        }

        // -- IsValidLoadTimeMs: drop (not clamp) rules for ReportMapLoad --

        [Test]
        public void IsValidLoadTimeMs_ZeroAndPositive_Valid()
        {
            Assert.That(MapLoadTimer.IsValidLoadTimeMs(0.0), Is.True);
            Assert.That(MapLoadTimer.IsValidLoadTimeMs(1234.5), Is.True);
        }

        [Test]
        public void IsValidLoadTimeMs_NegativeNaNInfinity_Invalid()
        {
            Assert.That(MapLoadTimer.IsValidLoadTimeMs(-0.001), Is.False);
            Assert.That(MapLoadTimer.IsValidLoadTimeMs(double.NaN), Is.False);
            Assert.That(MapLoadTimer.IsValidLoadTimeMs(double.PositiveInfinity), Is.False);
            Assert.That(MapLoadTimer.IsValidLoadTimeMs(double.NegativeInfinity), Is.False);
        }
    }
}
