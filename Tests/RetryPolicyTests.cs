using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class RetryPolicyTests
    {
        private RetryPolicy _policy;

        [SetUp]
        public void SetUp()
        {
            _policy = new RetryPolicy(maxRetries: 5, baseDelaySeconds: 1f);
        }

        // -- IsNonRetryableError --

        [Test]
        public void IsNonRetryableError_400_ReturnsTrue()
        {
            Assert.That(_policy.IsNonRetryableError(400), Is.True);
            Assert.That(_policy.IsNonRetryableError(401), Is.True);
            Assert.That(_policy.IsNonRetryableError(403), Is.True);
            Assert.That(_policy.IsNonRetryableError(404), Is.True);
            Assert.That(_policy.IsNonRetryableError(422), Is.True);
        }

        [Test]
        public void IsNonRetryableError_413_ReturnsFalse()
        {
            // 413 triggers batch split, not a non-retryable error
            Assert.That(_policy.IsNonRetryableError(413), Is.False);
        }

        [Test]
        public void IsNonRetryableError_429_ReturnsFalse()
        {
            // 429 is retryable
            Assert.That(_policy.IsNonRetryableError(429), Is.False);
        }

        [Test]
        public void IsNonRetryableError_5xx_ReturnsFalse()
        {
            Assert.That(_policy.IsNonRetryableError(500), Is.False);
            Assert.That(_policy.IsNonRetryableError(503), Is.False);
        }

        // -- ShouldSplitBatch --

        [Test]
        public void ShouldSplitBatch_413_MultipleEvents_ReturnsTrue()
        {
            Assert.That(_policy.ShouldSplitBatch(413, 2), Is.True);
            Assert.That(_policy.ShouldSplitBatch(413, 100), Is.True);
        }

        [Test]
        public void ShouldSplitBatch_413_SingleEvent_ReturnsFalse()
        {
            // Cannot split a single event
            Assert.That(_policy.ShouldSplitBatch(413, 1), Is.False);
            Assert.That(_policy.ShouldSplitBatch(413, 0), Is.False);
        }

        [Test]
        public void ShouldSplitBatch_Non413_ReturnsFalse()
        {
            Assert.That(_policy.ShouldSplitBatch(400, 10), Is.False);
            Assert.That(_policy.ShouldSplitBatch(500, 10), Is.False);
            Assert.That(_policy.ShouldSplitBatch(200, 10), Is.False);
        }

        // -- GetRetryDelaySeconds --

        [Test]
        public void GetRetryDelaySeconds_ExponentialBackoff()
        {
            Assert.That(_policy.GetRetryDelaySeconds(0), Is.EqualTo(1f));
            Assert.That(_policy.GetRetryDelaySeconds(1), Is.EqualTo(2f));
            Assert.That(_policy.GetRetryDelaySeconds(2), Is.EqualTo(4f));
            Assert.That(_policy.GetRetryDelaySeconds(3), Is.EqualTo(8f));
            Assert.That(_policy.GetRetryDelaySeconds(4), Is.EqualTo(16f));
        }

        [Test]
        public void GetRetryDelaySeconds_LargeAttempt_DoesNotOverflow()
        {
            // float max is ~3.4e38; 2^50 = ~1.1e15 -- well within range
            float delay = _policy.GetRetryDelaySeconds(50);
            Assert.That(float.IsInfinity(delay), Is.False);
            Assert.That(float.IsNaN(delay), Is.False);
            Assert.That(delay, Is.GreaterThan(0f));
        }

        [Test]
        public void GetRetryDelaySeconds_NegativeAttempt_TreatsAsZero()
        {
            Assert.That(_policy.GetRetryDelaySeconds(-1), Is.EqualTo(1f));
        }

        [Test]
        public void GetRetryDelaySeconds_CustomBase_ScalesCorrectly()
        {
            var policy = new RetryPolicy(maxRetries: 3, baseDelaySeconds: 0.5f);
            Assert.That(policy.GetRetryDelaySeconds(0), Is.EqualTo(0.5f));
            Assert.That(policy.GetRetryDelaySeconds(1), Is.EqualTo(1f));
            Assert.That(policy.GetRetryDelaySeconds(2), Is.EqualTo(2f));
        }

        // -- Classify --

        [Test]
        public void Classify_2xx_ReturnsSuccess()
        {
            Assert.That(_policy.Classify(200, 0, 10), Is.EqualTo(RetryAction.Success));
            Assert.That(_policy.Classify(204, 0, 10), Is.EqualTo(RetryAction.Success));
        }

        [Test]
        public void Classify_413_MultipleEvents_ReturnsSplitBatch()
        {
            Assert.That(_policy.Classify(413, 0, 10), Is.EqualTo(RetryAction.SplitBatch));
        }

        [Test]
        public void Classify_413_SingleEvent_ReturnsFail()
        {
            // Cannot split a single event -- treat as non-retryable
            Assert.That(_policy.Classify(413, 0, 1), Is.EqualTo(RetryAction.Fail));
        }

        [Test]
        public void Classify_400_ReturnsFail()
        {
            Assert.That(_policy.Classify(400, 0, 10), Is.EqualTo(RetryAction.Fail));
            Assert.That(_policy.Classify(401, 0, 10), Is.EqualTo(RetryAction.Fail));
        }

        [Test]
        public void Classify_500_WithAttemptsLeft_ReturnsRetry()
        {
            Assert.That(_policy.Classify(500, 0, 10), Is.EqualTo(RetryAction.Retry));
            Assert.That(_policy.Classify(500, 4, 10), Is.EqualTo(RetryAction.Retry));
        }

        [Test]
        public void Classify_500_NoAttemptsLeft_ReturnsFail()
        {
            Assert.That(_policy.Classify(500, 5, 10), Is.EqualTo(RetryAction.Fail));
        }

        [Test]
        public void Classify_429_WithAttemptsLeft_ReturnsRetry()
        {
            Assert.That(_policy.Classify(429, 0, 10), Is.EqualTo(RetryAction.Retry));
        }

        [Test]
        public void Classify_429_NoAttemptsLeft_ReturnsFail()
        {
            Assert.That(_policy.Classify(429, 5, 10), Is.EqualTo(RetryAction.Fail));
        }

        [Test]
        public void Classify_NetworkError_WithAttemptsLeft_ReturnsRetry()
        {
            Assert.That(_policy.Classify(0, 0, 10), Is.EqualTo(RetryAction.Retry));
        }

        [Test]
        public void Classify_3xx_ReturnsRetry_WhenAttemptsLeft()
        {
            // 3xx with redirectLimit=0 retries to match original fall-through
            Assert.That(_policy.Classify(301, 0, 10), Is.EqualTo(RetryAction.Retry));
            Assert.That(_policy.Classify(302, 0, 10), Is.EqualTo(RetryAction.Retry));
        }

        [Test]
        public void Classify_3xx_ReturnsFail_WhenAttemptsExhausted()
        {
            Assert.That(_policy.Classify(301, 5, 10), Is.EqualTo(RetryAction.Fail));
        }

        // -- Constructor defaults --

        [Test]
        public void Constructor_DefaultValues()
        {
            var policy = new RetryPolicy();
            Assert.That(policy.MaxRetries, Is.EqualTo(5));
            Assert.That(policy.BaseDelaySeconds, Is.EqualTo(1f));
        }

        [Test]
        public void Constructor_InvalidValues_FallsBackToDefaults()
        {
            var policy = new RetryPolicy(maxRetries: 0, baseDelaySeconds: -1f);
            Assert.That(policy.MaxRetries, Is.EqualTo(5));
            Assert.That(policy.BaseDelaySeconds, Is.EqualTo(1f));
        }

        [Test]
        public void Constructor_CustomValues()
        {
            var policy = new RetryPolicy(maxRetries: 3, baseDelaySeconds: 2f);
            Assert.That(policy.MaxRetries, Is.EqualTo(3));
            Assert.That(policy.BaseDelaySeconds, Is.EqualTo(2f));
        }
    }
}
