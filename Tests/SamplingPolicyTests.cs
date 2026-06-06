using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class SamplingPolicyTests
    {
        [Test]
        public void Rate1_AlwaysSamples()
        {
            var policy = new Framedash.SamplingPolicy(1.0f);
            for (int i = 0; i < 100; i++)
                Assert.That(policy.ShouldSample("evt"), Is.True);
        }

        [Test]
        public void Rate0_NeverSamples()
        {
            var policy = new Framedash.SamplingPolicy(0.0f);
            for (int i = 0; i < 100; i++)
                Assert.That(policy.ShouldSample("evt"), Is.False);
        }

        [Test]
        public void Rate_ClampedToUnitInterval()
        {
            var high = new Framedash.SamplingPolicy(2.0f);
            Assert.That(high.Rate, Is.EqualTo(1.0f));

            var low = new Framedash.SamplingPolicy(-1.0f);
            Assert.That(low.Rate, Is.EqualTo(0.0f));
        }

        [Test]
        public void PartialRate_ProducesApproximateRatio()
        {
            var policy = new Framedash.SamplingPolicy(0.5f);
            int sampled = 0;
            const int trials = 10000;

            for (int i = 0; i < trials; i++)
            {
                if (policy.ShouldSample("evt")) sampled++;
            }

            double ratio = (double)sampled / trials;
            // Allow wide tolerance for randomness: 0.35 to 0.65
            Assert.That(ratio, Is.InRange(0.35, 0.65),
                $"Expected ~50% sampling, got {ratio:P1}");
        }

        [Test]
        public void Rate_SetterClampsValue()
        {
            var policy = new Framedash.SamplingPolicy(0.5f);
            Assert.That(policy.Rate, Is.EqualTo(0.5f));

            policy.Rate = 1.5f;
            Assert.That(policy.Rate, Is.EqualTo(1.0f));

            policy.Rate = -0.5f;
            Assert.That(policy.Rate, Is.EqualTo(0.0f));

            policy.Rate = 0.75f;
            Assert.That(policy.Rate, Is.EqualTo(0.75f));
        }

        [Test]
        public void PerEventOverride_KeepsEventWhenGlobalDrops()
        {
            var policy = new Framedash.SamplingPolicy(0.0f);
            policy.SetEventRate("keep_me", 1.0f);

            for (int i = 0; i < 100; i++)
            {
                Assert.That(policy.ShouldSample("keep_me"), Is.True);
                Assert.That(policy.ShouldSample("other"), Is.False);
            }
        }

        [Test]
        public void PerEventOverride_DropsEventWhenGlobalKeeps()
        {
            var policy = new Framedash.SamplingPolicy(1.0f);
            policy.SetEventRate("drop_me", 0.0f);

            for (int i = 0; i < 100; i++)
            {
                Assert.That(policy.ShouldSample("drop_me"), Is.False);
                Assert.That(policy.ShouldSample("other"), Is.True);
            }
        }

        [Test]
        public void SetEventRate_ClampsToUnitInterval()
        {
            var policy = new Framedash.SamplingPolicy(0.5f);
            policy.SetEventRate("evt", 2.0f);
            for (int i = 0; i < 50; i++)
                Assert.That(policy.ShouldSample("evt"), Is.True);

            policy.SetEventRate("evt", -1.0f);
            for (int i = 0; i < 50; i++)
                Assert.That(policy.ShouldSample("evt"), Is.False);
        }

        [Test]
        public void RemoveEventRate_RestoresGlobalRate()
        {
            var policy = new Framedash.SamplingPolicy(1.0f);
            policy.SetEventRate("evt", 0.0f);
            Assert.That(policy.ShouldSample("evt"), Is.False);

            Assert.That(policy.RemoveEventRate("evt"), Is.True);
            Assert.That(policy.ShouldSample("evt"), Is.True);

            Assert.That(policy.RemoveEventRate("evt"), Is.False, "Removing absent override returns false");
        }

        [Test]
        public void SetEventRate_NullOrEmpty_Ignored()
        {
            var policy = new Framedash.SamplingPolicy(1.0f);
            policy.SetEventRate(null, 0.0f);
            policy.SetEventRate(string.Empty, 0.0f);
            Assert.That(policy.ShouldSample("anything"), Is.True);
        }

        [Test]
        public void ShouldSample_NullOrEmpty_UsesGlobalRate()
        {
            var keep = new Framedash.SamplingPolicy(1.0f);
            Assert.That(keep.ShouldSample(null), Is.True);
            Assert.That(keep.ShouldSample(string.Empty), Is.True);

            var drop = new Framedash.SamplingPolicy(0.0f);
            Assert.That(drop.ShouldSample(null), Is.False);
            Assert.That(drop.ShouldSample(string.Empty), Is.False);
        }
    }
}
