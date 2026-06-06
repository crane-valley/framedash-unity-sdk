using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class EndpointSecurityTests
    {
        [Test]
        public void Https_AnyHost_Allowed()
        {
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("https://ingest.framedash.dev/v1/events"), Is.True);
        }

        [Test]
        public void Http_LoopbackHosts_Allowed()
        {
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://localhost:8787/v1/events"), Is.True);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://127.0.0.1:8787/v1/events"), Is.True);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://[::1]:8787/v1/events"), Is.True);
        }

        [Test]
        public void Http_LookAlikeLoopbackSubdomain_Rejected()
        {
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://localhost.attacker.com/v1/events"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://127.0.0.1.attacker.com/v1/events"), Is.False);
        }

        [Test]
        public void Http_LoopbackInUserinfo_Rejected()
        {
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://localhost@evil.example/v1/events"), Is.False);
        }

        [Test]
        public void BackslashOrAtSign_Rejected()
        {
            // Parser differential vs libcurl: refuse '@' and '\' for any scheme.
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://localhost\\@evil.com/v1/events"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("https://localhost\\@evil.com/v1/events"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("https://localhost@evil.com/v1/events"), Is.False);
        }

        [Test]
        public void Http_NonLoopbackHost_Rejected()
        {
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://ingest.framedash.dev/v1/events"), Is.False);
        }

        [Test]
        public void Http_NonCanonicalLoopback_Rejected()
        {
            // Parity with the UE5 SDK's exact allowlist: only localhost / 127.0.0.1 /
            // [::1] get the cleartext-HTTP exemption. Other 127.0.0.0/8 addresses must
            // use HTTPS (still loopback, so this is consistency hardening, not a leak fix).
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://127.0.0.2/v1/events"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://127.0.0.10:8787/v1/events"), Is.False);
        }

        [Test]
        public void Http_AlternateLoopbackSpellings_Rejected()
        {
            // The check compares the RAW host text, not uri.Host, so System.Uri
            // canonicalization cannot turn an alternate spelling of 127.0.0.1 / ::1
            // into an allowed value. These all normalize to 127.0.0.1 / [::1] under
            // System.Uri (and genuinely resolve to loopback, so this is not a key
            // leak) but the UE5 SDK rejects them textually, so for parity we do too.
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://127.1/v1/events"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://0x7f000001/v1/events"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://2130706433/v1/events"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://0177.0.0.1/v1/events"), Is.False);
            // Expanded / IPv4-mapped / trailing-text IPv6 literals are rejected too.
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://[0:0:0:0:0:0:0:1]/v1/events"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://[::ffff:127.0.0.1]/v1/events"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("http://[::1].evil.example/v1/events"), Is.False);
            // HTTPS keeps the key encrypted regardless of host, so it stays allowed.
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("https://[::1]:8787/v1/events"), Is.True);
        }

        [Test]
        public void NonHttpScheme_Rejected()
        {
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("ftp://localhost/x"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("file:///etc/passwd"), Is.False);
        }

        [Test]
        public void ControlCharacters_Rejected()
        {
            // Build control chars at runtime to keep the source ASCII-clean.
            string withNul = "http://localhost" + (char)0 + ".evil.example/x";
            Assert.That(EndpointSecurity.IsEndpointTransportSecure(withNul), Is.False);
            string withCtrl = "https://ingest.framedash.dev/" + (char)1;
            Assert.That(EndpointSecurity.IsEndpointTransportSecure(withCtrl), Is.False);
        }

        [Test]
        public void MalformedOrEmpty_Rejected()
        {
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("not a url"), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure(""), Is.False);
            Assert.That(EndpointSecurity.IsEndpointTransportSecure(null), Is.False);
        }

        [Test]
        public void Https_LookAlikeLoopback_StillAllowed()
        {
            // Not loopback, but HTTPS keeps the key encrypted, so it is allowed.
            Assert.That(EndpointSecurity.IsEndpointTransportSecure("https://localhost.attacker.com/v1/events"), Is.True);
        }
    }
}
