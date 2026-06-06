using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class FlushPolicyTests
    {
        private FlushPolicy _policy;

        [SetUp]
        public void SetUp()
        {
            _policy = new FlushPolicy(
                maxBatchSize: 100,
                maxPayloadBytes: 102400,
                flushIntervalSeconds: 30f,
                bytesPerEventEstimate: 500);
        }

        // -- EstimatePayloadBytes --

        [Test]
        public void EstimatePayloadBytes_MultiplyByEstimate()
        {
            Assert.That(_policy.EstimatePayloadBytes(0), Is.EqualTo(0));
            Assert.That(_policy.EstimatePayloadBytes(1), Is.EqualTo(500));
            Assert.That(_policy.EstimatePayloadBytes(100), Is.EqualTo(50000));
            Assert.That(_policy.EstimatePayloadBytes(205), Is.EqualTo(102500));
        }

        // -- ShouldRequestFlush --

        [Test]
        public void ShouldRequestFlush_EventCountReachesBatchSize_ReturnsTrue()
        {
            Assert.That(_policy.ShouldRequestFlush(100, 0), Is.True);
            Assert.That(_policy.ShouldRequestFlush(200, 0), Is.True);
        }

        [Test]
        public void ShouldRequestFlush_EstimatedBytesReachesLimit_ReturnsTrue()
        {
            Assert.That(_policy.ShouldRequestFlush(1, 102400), Is.True);
            Assert.That(_policy.ShouldRequestFlush(1, 200000), Is.True);
        }

        [Test]
        public void ShouldRequestFlush_BelowBothThresholds_ReturnsFalse()
        {
            Assert.That(_policy.ShouldRequestFlush(99, 50000), Is.False);
            Assert.That(_policy.ShouldRequestFlush(0, 0), Is.False);
            Assert.That(_policy.ShouldRequestFlush(50, 51200), Is.False);
        }

        [Test]
        public void ShouldRequestFlush_ExactlyAtBatchSize_ReturnsTrue()
        {
            Assert.That(_policy.ShouldRequestFlush(100, 49999), Is.True);
        }

        [Test]
        public void ShouldRequestFlush_ExactlyAtPayloadLimit_ReturnsTrue()
        {
            Assert.That(_policy.ShouldRequestFlush(99, 102400), Is.True);
        }

        // -- ShouldFlush --

        [Test]
        public void ShouldFlush_FlushRequested_ReturnsTrue()
        {
            Assert.That(_policy.ShouldFlush(true, 0f), Is.True);
            Assert.That(_policy.ShouldFlush(true, 29f), Is.True);
        }

        [Test]
        public void ShouldFlush_IntervalElapsed_ReturnsTrue()
        {
            Assert.That(_policy.ShouldFlush(false, 30f), Is.True);
            Assert.That(_policy.ShouldFlush(false, 45f), Is.True);
        }

        [Test]
        public void ShouldFlush_NeitherCondition_ReturnsFalse()
        {
            Assert.That(_policy.ShouldFlush(false, 0f), Is.False);
            Assert.That(_policy.ShouldFlush(false, 29.9f), Is.False);
        }

        [Test]
        public void ShouldFlush_BothConditions_ReturnsTrue()
        {
            Assert.That(_policy.ShouldFlush(true, 30f), Is.True);
        }

        // -- Constructor defaults --

        [Test]
        public void Constructor_DefaultValues()
        {
            var policy = new FlushPolicy();
            Assert.That(policy.MaxBatchSize, Is.EqualTo(100));
            Assert.That(policy.MaxPayloadBytes, Is.EqualTo(102400));
            Assert.That(policy.FlushIntervalSeconds, Is.EqualTo(30f));
            Assert.That(policy.BytesPerEventEstimate, Is.EqualTo(500));
        }

        [Test]
        public void Constructor_InvalidValues_FallsBackToDefaults()
        {
            var policy = new FlushPolicy(
                maxBatchSize: 0,
                maxPayloadBytes: -1,
                flushIntervalSeconds: 0f,
                bytesPerEventEstimate: -100);
            Assert.That(policy.MaxBatchSize, Is.EqualTo(100));
            Assert.That(policy.MaxPayloadBytes, Is.EqualTo(102400));
            Assert.That(policy.FlushIntervalSeconds, Is.EqualTo(30f));
            Assert.That(policy.BytesPerEventEstimate, Is.EqualTo(500));
        }

        [Test]
        public void Constructor_CustomValues()
        {
            var policy = new FlushPolicy(
                maxBatchSize: 50,
                maxPayloadBytes: 51200,
                flushIntervalSeconds: 15f,
                bytesPerEventEstimate: 250);
            Assert.That(policy.MaxBatchSize, Is.EqualTo(50));
            Assert.That(policy.MaxPayloadBytes, Is.EqualTo(51200));
            Assert.That(policy.FlushIntervalSeconds, Is.EqualTo(15f));
            Assert.That(policy.BytesPerEventEstimate, Is.EqualTo(250));
        }

        // -- Edge cases --

        [Test]
        public void ShouldRequestFlush_SingleLargeEvent_TriggersOnBytes()
        {
            // Custom policy: 1 byte per event, max payload 10 bytes
            var policy = new FlushPolicy(
                maxBatchSize: 1000,
                maxPayloadBytes: 10,
                bytesPerEventEstimate: 1);
            Assert.That(policy.ShouldRequestFlush(10, 10), Is.True);
            Assert.That(policy.ShouldRequestFlush(9, 9), Is.False);
        }

        [Test]
        public void ShouldFlush_CustomInterval_RespectsValue()
        {
            var policy = new FlushPolicy(flushIntervalSeconds: 5f);
            Assert.That(policy.ShouldFlush(false, 4.9f), Is.False);
            Assert.That(policy.ShouldFlush(false, 5f), Is.True);
        }
    }
}
