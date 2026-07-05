using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class TransportLogTests
    {
        [Test]
        public void FormatFlushSuccess_IncludesCountAndStatus()
        {
            // F29 opt-in verbose success log: first-time integrators need a positive
            // client-side confirmation of delivery (count + status), mirroring the
            // UE5 SDK's "Batch sent successfully (HTTP N)" spirit.
            string message = TransportLog.FormatFlushSuccess(42, 202);
            Assert.That(message, Does.Contain("42"));
            Assert.That(message, Does.Contain("202"));
            Assert.That(message, Does.Contain("Framedash"));
        }

        [Test]
        public void FormatFlushSuccess_SingleEvent_UsesSingularNoun()
        {
            string message = TransportLog.FormatFlushSuccess(1, 200);
            Assert.That(message, Is.EqualTo("[Framedash] Flushed 1 event (HTTP 200)."));
        }

        [Test]
        public void FormatFlushSuccess_MultipleEvents_UsesPluralNoun()
        {
            string message = TransportLog.FormatFlushSuccess(3, 202);
            Assert.That(message, Is.EqualTo("[Framedash] Flushed 3 events (HTTP 202)."));
        }

        [Test]
        public void FormatSendAttempt_IncludesCountBytesAndEndpoint()
        {
            // The send-attempt line carries the endpoint + compressed payload size so an
            // integrator can confirm WHERE telemetry goes and HOW BIG each batch is.
            string message = TransportLog.FormatSendAttempt(
                5, 1234, "https://ingest.framedash.dev/v1/events");
            Assert.That(message, Does.Contain("5"));
            Assert.That(message, Does.Contain("1234"));
            Assert.That(message, Does.Contain("https://ingest.framedash.dev/v1/events"));
            Assert.That(message, Does.Contain("Framedash"));
        }

        [Test]
        public void FormatSendAttempt_SingleEvent_UsesSingularNoun()
        {
            string message = TransportLog.FormatSendAttempt(1, 88, "https://example.test/v1/events");
            Assert.That(message, Is.EqualTo(
                "[Framedash] Sending 1 event (88 bytes gzip) -> https://example.test/v1/events"));
        }

        [Test]
        public void FormatSendAttempt_MultipleEvents_UsesPluralNoun()
        {
            string message = TransportLog.FormatSendAttempt(2, 200, "https://example.test/v1/events");
            Assert.That(message, Is.EqualTo(
                "[Framedash] Sending 2 events (200 bytes gzip) -> https://example.test/v1/events"));
        }
    }
}
