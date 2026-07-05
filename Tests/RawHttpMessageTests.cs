using System.Text;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class RawHttpMessageTests
    {
        // -- BuildPostHead --

        [Test]
        public void BuildPostHead_EmitsRequestLineAndAllHeaders()
        {
            byte[] head = RawHttpMessage.BuildPostHead(
                "/v1/events", "ingest.framedash.dev", "fd_key_123", "unity-0.1.1", 42);
            string text = Encoding.ASCII.GetString(head);

            Assert.That(text, Does.StartWith("POST /v1/events HTTP/1.1\r\n"));
            Assert.That(text, Does.Contain("\r\nHost: ingest.framedash.dev\r\n"));
            Assert.That(text, Does.Contain("\r\nContent-Type: application/x-protobuf\r\n"));
            Assert.That(text, Does.Contain("\r\nContent-Encoding: gzip\r\n"));
            Assert.That(text, Does.Contain("\r\nX-API-Key: fd_key_123\r\n"));
            Assert.That(text, Does.Contain("\r\nX-SDK-Version: unity-0.1.1\r\n"));
            Assert.That(text, Does.Contain("\r\nContent-Length: 42\r\n"));
            Assert.That(text, Does.Contain("\r\nConnection: close\r\n"));
        }

        [Test]
        public void BuildPostHead_EndsWithBlankLine()
        {
            byte[] head = RawHttpMessage.BuildPostHead(
                "/v1/events", "ingest.framedash.dev", "k", "v", 0);
            string text = Encoding.ASCII.GetString(head);
            Assert.That(text, Does.EndWith("\r\n\r\n"));
            // Exactly one blank line: the head must not open the body early.
            Assert.That(text.IndexOf("\r\n\r\n", System.StringComparison.Ordinal),
                Is.EqualTo(text.Length - 4));
        }

        [Test]
        public void BuildPostHead_QueryPreservedInTarget()
        {
            byte[] head = RawHttpMessage.BuildPostHead(
                "/v1/events?debug=1", "ingest.framedash.dev", "k", "v", 1);
            Assert.That(Encoding.ASCII.GetString(head),
                Does.StartWith("POST /v1/events?debug=1 HTTP/1.1\r\n"));
        }

        [Test]
        public void BuildPostHead_EmptyTarget_FallsBackToRoot()
        {
            byte[] head = RawHttpMessage.BuildPostHead("", "h", "k", "v", 1);
            Assert.That(Encoding.ASCII.GetString(head), Does.StartWith("POST / HTTP/1.1\r\n"));
        }

        [Test]
        public void BuildPostHead_StripsCrLfFromHeaderValues()
        {
            // Request-smuggling hygiene: a CR/LF in a developer-supplied value must
            // not be able to inject an extra header line.
            byte[] head = RawHttpMessage.BuildPostHead(
                "/v1/events", "ingest.framedash.dev", "key\r\nX-Evil: 1", "v\n2", 1);
            string text = Encoding.ASCII.GetString(head);
            Assert.That(text, Does.Contain("\r\nX-API-Key: keyX-Evil: 1\r\n"));
            Assert.That(text, Does.Contain("\r\nX-SDK-Version: v2\r\n"));
        }

        [Test]
        public void BuildPostHead_TargetWithSpace_CannotSplitRequestLine()
        {
            // The request line is space-delimited: a SP in the target would split it
            // into a bogus method/target/version triple. Must be stripped.
            byte[] head = RawHttpMessage.BuildPostHead(
                "/v1/events HTTP/1.1\r\nX-Evil: 1\r\n\r\nGET /steal", "h", "k", "v", 1);
            string text = Encoding.ASCII.GetString(head);
            Assert.That(text, Does.StartWith("POST /v1/eventsHTTP/1.1X-Evil:1GET/steal HTTP/1.1\r\n"));
            Assert.That(text, Does.Not.Contain("X-Evil: 1"));
        }

        // -- SanitizeRequestTarget --

        [Test]
        public void SanitizeRequestTarget_CleanOriginForm_Unchanged()
        {
            Assert.That(RawHttpMessage.SanitizeRequestTarget("/v1/events?debug=1"),
                Is.EqualTo("/v1/events?debug=1"));
        }

        [Test]
        public void SanitizeRequestTarget_StripsSpacesAndControls()
        {
            Assert.That(RawHttpMessage.SanitizeRequestTarget("/v1/ev ents\r\n"),
                Is.EqualTo("/v1/events"));
            Assert.That(RawHttpMessage.SanitizeRequestTarget("/a\tb" + (char)0x7F + "c"),
                Is.EqualTo("/abc"));
        }

        [Test]
        public void SanitizeRequestTarget_ForcesLeadingSlash()
        {
            // Origin-form (RFC 9112 3.2.1) must start with "/".
            Assert.That(RawHttpMessage.SanitizeRequestTarget("v1/events"), Is.EqualTo("/v1/events"));
        }

        [Test]
        public void SanitizeRequestTarget_NullEmptyOrAllStripped_ReturnsRoot()
        {
            Assert.That(RawHttpMessage.SanitizeRequestTarget(null), Is.EqualTo("/"));
            Assert.That(RawHttpMessage.SanitizeRequestTarget(""), Is.EqualTo("/"));
            Assert.That(RawHttpMessage.SanitizeRequestTarget(" \r\n "), Is.EqualTo("/"));
        }

        // -- SanitizeHeaderValue --

        [Test]
        public void SanitizeHeaderValue_CleanValue_ReturnedUnchanged()
        {
            Assert.That(RawHttpMessage.SanitizeHeaderValue("fd_key_123"), Is.EqualTo("fd_key_123"));
        }

        [Test]
        public void SanitizeHeaderValue_RemovesControlCharacters()
        {
            // DEL is appended as (char)0x7F: a "\x7F" string escape would greedily
            // consume a following hex digit (e.g. "\x7Fd" parses as U+07FD).
            Assert.That(
                RawHttpMessage.SanitizeHeaderValue("a\r\nb\tc" + (char)0x7F + "d"),
                Is.EqualTo("abcd"));
        }

        [Test]
        public void SanitizeHeaderValue_NullOrEmpty_ReturnsEmpty()
        {
            Assert.That(RawHttpMessage.SanitizeHeaderValue(null), Is.Empty);
            Assert.That(RawHttpMessage.SanitizeHeaderValue(""), Is.Empty);
        }

        // -- TryParseStatusCode --

        private static bool Parse(string text, out long code)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            return RawHttpMessage.TryParseStatusCode(bytes, bytes.Length, out code);
        }

        [Test]
        public void TryParseStatusCode_AcceptedResponse_Parses202()
        {
            Assert.That(Parse("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\n\r\n", out long code), Is.True);
            Assert.That(code, Is.EqualTo(202));
        }

        [Test]
        public void TryParseStatusCode_ErrorStatus_Parses()
        {
            Assert.That(Parse("HTTP/1.1 413 Payload Too Large\r\n", out long code), Is.True);
            Assert.That(code, Is.EqualTo(413));

            Assert.That(Parse("HTTP/1.1 500 Internal Server Error\r\n", out code), Is.True);
            Assert.That(code, Is.EqualTo(500));
        }

        [Test]
        public void TryParseStatusCode_NoReasonPhrase_Parses()
        {
            Assert.That(Parse("HTTP/1.1 204\r\n", out long code), Is.True);
            Assert.That(code, Is.EqualTo(204));
        }

        [Test]
        public void TryParseStatusCode_Http10StatusLine_Parses()
        {
            // An intermediary may answer HTTP/1.0; the version token is not checked.
            Assert.That(Parse("HTTP/1.0 502 Bad Gateway\r\n", out long code), Is.True);
            Assert.That(code, Is.EqualTo(502));
        }

        [Test]
        public void TryParseStatusCode_IncompleteLine_False()
        {
            // No LF yet: the status line may still be arriving -- keep reading.
            Assert.That(Parse("HTTP/1.1 20", out long code), Is.False);
            Assert.That(code, Is.Zero);
        }

        [Test]
        public void TryParseStatusCode_NotHttp_False()
        {
            Assert.That(Parse("SSH-2.0-OpenSSH\r\n", out long code), Is.False);
            Assert.That(code, Is.Zero);
        }

        [Test]
        public void TryParseStatusCode_MalformedCode_False()
        {
            Assert.That(Parse("HTTP/1.1 xx\r\n", out _), Is.False);
            Assert.That(Parse("HTTP/1.1 20 OK\r\n", out _), Is.False);
            Assert.That(Parse("HTTP/1.1 2022 OK\r\n", out _), Is.False);
            Assert.That(Parse("HTTP/1.1 \r\n", out _), Is.False);
            Assert.That(Parse("HTTP/1.1\r\n", out _), Is.False);
        }

        [Test]
        public void TryParseStatusCode_RespectsCountOverBufferContent()
        {
            // Bytes past count must be invisible (the read loop passes a partially
            // filled buffer).
            byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 202 Accepted\r\n");
            Assert.That(RawHttpMessage.TryParseStatusCode(bytes, 5, out long code), Is.False);
            Assert.That(code, Is.Zero);
            Assert.That(RawHttpMessage.TryParseStatusCode(bytes, bytes.Length, out code), Is.True);
            Assert.That(code, Is.EqualTo(202));
        }

        [Test]
        public void TryParseStatusCode_NullBuffer_False()
        {
            Assert.That(RawHttpMessage.TryParseStatusCode(null, 0, out long code), Is.False);
            Assert.That(code, Is.Zero);
        }

        [Test]
        public void TryParseStatusCode_CountLargerThanBuffer_ClampsSafely()
        {
            byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 202\r\n");
            Assert.That(RawHttpMessage.TryParseStatusCode(bytes, bytes.Length + 100, out long code), Is.True);
            Assert.That(code, Is.EqualTo(202));
        }
    }
}
