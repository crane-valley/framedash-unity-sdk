using Framedash.Editor.Logic;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class FramedashEditorEndpointSecurityTests
    {
        [Test]
        public void SameOrigin_ReturnsFalse()
        {
            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect(
                    "https://API.Example.com/projects",
                    "https://api.example.com:443/other"),
                Is.False);
        }

        [Test]
        public void DifferentHost_ReturnsTrue()
        {
            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect(
                    "https://api.example.com",
                    "https://evil.example.com"),
                Is.True);
        }

        [Test]
        public void DifferentPort_ReturnsTrue()
        {
            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect(
                    "https://a.com",
                    "https://a.com:8443"),
                Is.True);
        }

        [Test]
        public void DifferentScheme_ReturnsTrue()
        {
            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect(
                    "http://a.com",
                    "https://a.com"),
                Is.True);
        }

        [Test]
        public void EmptyEffectiveUrl_ReturnsFalse()
        {
            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect("https://a.com", ""),
                Is.False);
        }

        [TestCase("a.com", "https://a.com")]
        [TestCase("https://a.com", "a.com")]
        [TestCase("ftp://a.com", "https://a.com")]
        [TestCase("https://a.com", "ftp://a.com")]
        public void UnparseableOrigin_ReturnsTrue(string configuredUrl, string effectiveUrl)
        {
            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect(configuredUrl, effectiveUrl),
                Is.True);
        }

        [Test]
        public void AtSignInEitherUrl_ReturnsTrue()
        {
            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect(
                    "https://user@a.com",
                    "https://a.com"),
                Is.True);
            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect(
                    "https://a.com",
                    "https://user@a.com"),
                Is.True);
        }

        [Test]
        public void ControlCharacterInEitherUrl_ReturnsTrue()
        {
            string configured = "https://a.com/" + (char)1;
            string effective = "https://a.com/" + (char)0x7f;

            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect(configured, "https://a.com"),
                Is.True);
            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect("https://a.com", effective),
                Is.True);
        }

        [Test]
        public void BracketedIpv6WithDefaultPort_IsSameOrigin()
        {
            Assert.That(
                FramedashEditorEndpointSecurity.IsCrossOriginRedirect(
                    "https://[::1]/maps",
                    "https://[::1]:443/heatmap"),
                Is.False);
        }
    }
}
