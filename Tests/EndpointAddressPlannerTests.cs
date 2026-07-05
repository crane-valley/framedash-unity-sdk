using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class EndpointAddressPlannerTests
    {
        private const string Endpoint = "https://ingest.framedash.dev/v1/events";
        private const string Ipv4 = "104.18.0.1";
        private const string Ipv6 = "2606:4700::1";

        // -- PreferenceOrder --

        [Test]
        public void PreferenceOrder_IsIPv4ThenIPv6()
        {
            // Broken-IPv6 is the dominant real-world failure; prefer IPv4 first.
            Assert.That(EndpointAddressPlanner.PreferenceOrder,
                Is.EqualTo(new[] { IpFamily.IPv4, IpFamily.IPv6 }));
        }

        // -- ShouldForceAddressFamily --

        [Test]
        public void ShouldForceAddressFamily_RemoteHttpsHostname_True()
        {
            Assert.That(EndpointAddressPlanner.ShouldForceAddressFamily(Endpoint), Is.True);
        }

        [Test]
        public void ShouldForceAddressFamily_Localhost_False()
        {
            // Loopback has no IPv6-blackhole exposure; forcing would only risk local dev.
            Assert.That(EndpointAddressPlanner.ShouldForceAddressFamily("https://localhost/v1/events"), Is.False);
            Assert.That(EndpointAddressPlanner.ShouldForceAddressFamily("https://LOCALHOST:8443/v1/events"), Is.False);
        }

        [Test]
        public void ShouldForceAddressFamily_Http_False()
        {
            // Plain HTTP is loopback-only per EndpointSecurity -- never force it.
            Assert.That(EndpointAddressPlanner.ShouldForceAddressFamily("http://127.0.0.1:8787/v1/events"), Is.False);
        }

        [Test]
        public void ShouldForceAddressFamily_IpLiteralEndpoint_False()
        {
            // Already pinned to one address; nothing to resolve or reorder.
            Assert.That(EndpointAddressPlanner.ShouldForceAddressFamily("https://104.18.0.1/v1/events"), Is.False);
            Assert.That(EndpointAddressPlanner.ShouldForceAddressFamily("https://[2606:4700::1]/v1/events"), Is.False);
        }

        [Test]
        public void ShouldForceAddressFamily_NullOrEmptyOrMalformed_False()
        {
            Assert.That(EndpointAddressPlanner.ShouldForceAddressFamily(null), Is.False);
            Assert.That(EndpointAddressPlanner.ShouldForceAddressFamily(""), Is.False);
            Assert.That(EndpointAddressPlanner.ShouldForceAddressFamily("not a url"), Is.False);
        }

        // -- RewriteHost --

        [Test]
        public void RewriteHost_IPv4_SubstitutesHostKeepsPath()
        {
            Assert.That(EndpointAddressPlanner.RewriteHost(Endpoint, Ipv4, IpFamily.IPv4),
                Is.EqualTo("https://104.18.0.1/v1/events"));
        }

        [Test]
        public void RewriteHost_IPv6_BracketsLiteral()
        {
            Assert.That(EndpointAddressPlanner.RewriteHost(Endpoint, Ipv6, IpFamily.IPv6),
                Is.EqualTo("https://[2606:4700::1]/v1/events"));
        }

        [Test]
        public void RewriteHost_IPv6_DoesNotDoubleBracket()
        {
            // IPAddress.ToString() returns a bare IPv6, but guard against a pre-bracketed input.
            Assert.That(EndpointAddressPlanner.RewriteHost(Endpoint, "[2606:4700::1]", IpFamily.IPv6),
                Is.EqualTo("https://[2606:4700::1]/v1/events"));
        }

        [Test]
        public void RewriteHost_NonDefaultPort_Preserved()
        {
            Assert.That(
                EndpointAddressPlanner.RewriteHost("https://ingest.framedash.dev:8443/v1/events", Ipv4, IpFamily.IPv4),
                Is.EqualTo("https://104.18.0.1:8443/v1/events"));
        }

        [Test]
        public void RewriteHost_EmptyOrNullIpLiteral_ReturnsOriginalUnchanged()
        {
            // Public-API guard: nothing to substitute -> return the URL unchanged.
            Assert.That(EndpointAddressPlanner.RewriteHost(Endpoint, "", IpFamily.IPv4), Is.EqualTo(Endpoint));
            Assert.That(EndpointAddressPlanner.RewriteHost(Endpoint, null!, IpFamily.IPv6), Is.EqualTo(Endpoint));
        }

        [Test]
        public void RewriteHost_QueryPreserved()
        {
            Assert.That(
                EndpointAddressPlanner.RewriteHost("https://ingest.framedash.dev/v1/events?debug=1", Ipv4, IpFamily.IPv4),
                Is.EqualTo("https://104.18.0.1/v1/events?debug=1"));
        }

        // -- HostHeaderValue --

        [Test]
        public void HostHeaderValue_DefaultPort_OmitsPort()
        {
            Assert.That(EndpointAddressPlanner.HostHeaderValue(Endpoint), Is.EqualTo("ingest.framedash.dev"));
        }

        [Test]
        public void HostHeaderValue_NonDefaultPort_IncludesPort()
        {
            Assert.That(EndpointAddressPlanner.HostHeaderValue("https://ingest.framedash.dev:8443/v1/events"),
                Is.EqualTo("ingest.framedash.dev:8443"));
        }

        // -- Build: family ordering --

        [Test]
        public void Build_BothFamilies_IPv4FirstThenIPv6()
        {
            var plan = EndpointAddressPlanner.Build(Endpoint, Ipv4, Ipv6);
            Assert.That(plan.IsPassthrough, Is.False);
            Assert.That(plan.AttemptUrls, Is.EqualTo(new[]
            {
                "https://104.18.0.1/v1/events",
                "https://[2606:4700::1]/v1/events",
            }));
            Assert.That(plan.HostHeader, Is.EqualTo("ingest.framedash.dev"));
            Assert.That(plan.CommonName, Is.EqualTo("ingest.framedash.dev"));
        }

        [Test]
        public void Build_IPv4Only_SingleIPv4Attempt()
        {
            // Common broken-IPv6 outcome once IPv6 resolution is skipped/empty.
            var plan = EndpointAddressPlanner.Build(Endpoint, Ipv4, "");
            Assert.That(plan.IsPassthrough, Is.False);
            Assert.That(plan.AttemptUrls, Is.EqualTo(new[] { "https://104.18.0.1/v1/events" }));
        }

        [Test]
        public void Build_IPv6Only_SingleIPv6Attempt()
        {
            // IPv6-only network: no A record resolved, deliver over IPv6.
            var plan = EndpointAddressPlanner.Build(Endpoint, "", Ipv6);
            Assert.That(plan.IsPassthrough, Is.False);
            Assert.That(plan.AttemptUrls, Is.EqualTo(new[] { "https://[2606:4700::1]/v1/events" }));
            Assert.That(plan.CommonName, Is.EqualTo("ingest.framedash.dev"));
        }

        [Test]
        public void Build_NoResolution_PassthroughOnOriginalUrl()
        {
            // Total DNS failure: fall back to the FQDN URL so the engine can still try,
            // rather than the SDK dropping the batch outright.
            var plan = EndpointAddressPlanner.Build(Endpoint, "", "");
            Assert.That(plan.IsPassthrough, Is.True);
            Assert.That(plan.AttemptUrls, Is.EqualTo(new[] { Endpoint }));
            Assert.That(plan.HostHeader, Is.Empty);
            Assert.That(plan.CommonName, Is.Empty);
        }

        [Test]
        public void Build_NullResolution_Passthrough()
        {
            var plan = EndpointAddressPlanner.Build(Endpoint, null, null);
            Assert.That(plan.IsPassthrough, Is.True);
            Assert.That(plan.AttemptUrls, Is.EqualTo(new[] { Endpoint }));
        }

        [Test]
        public void Build_LoopbackEndpoint_PassthroughIgnoresResolvedIps()
        {
            // A loopback endpoint is never rewritten even if addresses are supplied.
            var plan = EndpointAddressPlanner.Build("https://localhost:8443/v1/events", Ipv4, Ipv6);
            Assert.That(plan.IsPassthrough, Is.True);
            Assert.That(plan.AttemptUrls, Is.EqualTo(new[] { "https://localhost:8443/v1/events" }));
        }

        [Test]
        public void Build_IpLiteralEndpoint_Passthrough()
        {
            var plan = EndpointAddressPlanner.Build("https://104.18.0.1/v1/events", Ipv4, Ipv6);
            Assert.That(plan.IsPassthrough, Is.True);
        }

        // -- NextFamily (toggle/wrap on transport-level failure) --

        [Test]
        public void NextFamily_TwoFamilies_TogglesBackAndForth()
        {
            // IPv4(0) -> IPv6(1) -> back to IPv4(0). Wrapping (not advance-and-clamp) is
            // what lets a broken-IPv6 network return to working IPv4 after a transient
            // IPv4 glitch pushed selection onto the IPv6 blackhole.
            Assert.That(EndpointAddressPlanner.NextFamily(0, 2), Is.EqualTo(1));
            Assert.That(EndpointAddressPlanner.NextFamily(1, 2), Is.EqualTo(0));
        }

        [Test]
        public void NextFamily_SingleUrl_StaysZero()
        {
            // Passthrough plan (one URL): the modulo keeps the index at 0 (no-op).
            Assert.That(EndpointAddressPlanner.NextFamily(0, 1), Is.EqualTo(0));
        }

        [Test]
        public void NextFamily_EmptyOrNonPositiveCount_ReturnsZero()
        {
            Assert.That(EndpointAddressPlanner.NextFamily(0, 0), Is.EqualTo(0));
            Assert.That(EndpointAddressPlanner.NextFamily(3, -1), Is.EqualTo(0));
        }

        // -- Immutability hardening --

        [Test]
        public void PreferenceOrder_IsReadOnly()
        {
            Assert.That(((IList)EndpointAddressPlanner.PreferenceOrder).IsReadOnly, Is.True);
        }

        [Test]
        public void AttemptUrls_IsReadOnly()
        {
            var plan = EndpointAddressPlanner.Build(Endpoint, Ipv4, Ipv6);
            Assert.That(((IList)plan.AttemptUrls).IsReadOnly, Is.True);
        }

        [Test]
        public void AttemptUrls_IsDefensiveCopy_CallerMutationDoesNotLeakIn()
        {
            // Constructing with a mutable list and mutating it afterwards must not affect
            // the plan (the plan stores a read-only snapshot, not the caller's reference).
            var mutable = new List<string> { "https://104.18.0.1/v1/events" };
            var plan = new EndpointAddressPlan(mutable, "ingest.framedash.dev", "ingest.framedash.dev");
            mutable.Add("https://evil.example/v1/events");
            Assert.That(plan.AttemptUrls.Count, Is.EqualTo(1));
            Assert.That(plan.AttemptUrls[0], Is.EqualTo("https://104.18.0.1/v1/events"));
        }
    }
}
