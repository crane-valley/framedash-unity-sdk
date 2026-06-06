using System.Collections.Generic;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class BatchPolicyTests
    {
        // Build a batch of `count` events, each carrying `attrsPerEvent` attribute
        // entries and `metricsPerEvent` metric entries, to exercise the decoded-entry
        // accounting (events + all map entries).
        private static TelemetryEvent[] MakeBatch(int count, int attrsPerEvent = 0, int metricsPerEvent = 0)
        {
            var batch = new TelemetryEvent[count];
            for (int i = 0; i < count; i++)
            {
                List<StringPair> attrs = null;
                if (attrsPerEvent > 0)
                {
                    attrs = new List<StringPair>(attrsPerEvent);
                    for (int a = 0; a < attrsPerEvent; a++) attrs.Add(new StringPair("k" + a, "v"));
                }
                List<FloatPair> metrics = null;
                if (metricsPerEvent > 0)
                {
                    metrics = new List<FloatPair>(metricsPerEvent);
                    for (int m = 0; m < metricsPerEvent; m++) metrics.Add(new FloatPair("m" + m, m));
                }
                batch[i] = new TelemetryEvent { EventName = "e", Attributes = attrs, Metrics = metrics };
            }
            return batch;
        }

        [Test]
        public void DoesNotSplitAtFlushBatchThreshold()
        {
            // Regression guard: the split must key off the server wire caps
            // (BatchPolicy.MaxEventsPerBatch / MaxDecodedEntries), NOT the per-flush
            // batch size. A previous version capped the split at the flush batch
            // size, so a stall/burst drain was fragmented into many small requests.
            // A plain drain well below the wire caps must not split.
            Assert.That(BatchPolicy.ExceedsWireCaps(MakeBatch(100)), Is.False);
            Assert.That(BatchPolicy.ExceedsWireCaps(MakeBatch(BatchPolicy.MaxEventsPerBatch / 2)), Is.False);
            Assert.That(BatchPolicy.ExceedsWireCaps(MakeBatch(BatchPolicy.MaxEventsPerBatch - 1)), Is.False);
        }

        [Test]
        public void DoesNotSplitAtOrBelowEventCap()
        {
            Assert.That(BatchPolicy.ExceedsWireCaps(MakeBatch(BatchPolicy.MaxEventsPerBatch)), Is.False);
        }

        [Test]
        public void SplitsAboveEventCap()
        {
            Assert.That(BatchPolicy.ExceedsWireCaps(MakeBatch(BatchPolicy.MaxEventsPerBatch + 1)), Is.True);
        }

        [Test]
        public void SplitsWhenDecodedEntriesExceedCapEvenBelowEventCap()
        {
            // The case Codex flagged: 10,000 events (== the event cap, so the event
            // count alone does NOT trigger a split) each with 10 attributes is
            // 10,000 + 100,000 = 110,000 decoded entries > MaxDecodedEntries, which
            // the server rejects wholesale. The decoded-entry cap must trigger the
            // split even though every per-event count and the event count are in range.
            var batch = MakeBatch(BatchPolicy.MaxEventsPerBatch, attrsPerEvent: 10);
            Assert.That(batch.Length, Is.LessThanOrEqualTo(BatchPolicy.MaxEventsPerBatch));
            Assert.That(BatchPolicy.CountDecodedEntries(batch), Is.GreaterThan(BatchPolicy.MaxDecodedEntries));
            Assert.That(BatchPolicy.ExceedsWireCaps(batch), Is.True);
        }

        [Test]
        public void HalvingABatchBringsDecodedEntriesUnderCap()
        {
            // The recursive SplitAndResend halves the batch; one split must take the
            // 110,000-entry batch under the cap so it converges, not loop.
            var half = MakeBatch(BatchPolicy.MaxEventsPerBatch / 2, attrsPerEvent: 10);
            Assert.That(BatchPolicy.ExceedsWireCaps(half), Is.False);
        }

        [Test]
        public void CountsEventsPlusAttributesPlusMetrics()
        {
            // 3 events, 2 attrs + 1 metric each => 3 + 6 + 3 = 12.
            var batch = MakeBatch(3, attrsPerEvent: 2, metricsPerEvent: 1);
            Assert.That(BatchPolicy.CountDecodedEntries(batch), Is.EqualTo(12));
        }

        [Test]
        public void NeverSplitsTrivialBatches()
        {
            // A single event (or empty) is never split: it is bounded by the
            // payload-byte path or dropped on a 413, not by the event-count cap, and
            // the server enforces the per-event attribute/metric caps regardless.
            Assert.That(BatchPolicy.ExceedsWireCaps(MakeBatch(0)), Is.False);
            Assert.That(BatchPolicy.ExceedsWireCaps(MakeBatch(1)), Is.False);
            Assert.That(BatchPolicy.ExceedsWireCaps(MakeBatch(1, attrsPerEvent: 200000)), Is.False);
            Assert.That(BatchPolicy.ExceedsWireCaps(null), Is.False);
        }
    }
}
