using System;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class SessionManagerTests
    {
        [Test]
        public void SessionId_IsValidGuid()
        {
            var mgr = new Framedash.SessionManager();
            Assert.That(Guid.TryParse(mgr.SessionId, out _), Is.True);
        }

        [Test]
        public void SessionId_IsRfc9562V7()
        {
            // Canonical 8-4-4-4-12 form: version nibble at index 14
            // (group 3's first hex char) and variant nibble at index 19
            // (group 4's first hex char, must be one of 8/9/a/b).
            // Guard the canonical shape before indexing so a regression
            // to a non-GUID string fails loudly instead of throwing
            // IndexOutOfRangeException with no context.
            var mgr = new Framedash.SessionManager();
            string id = mgr.SessionId;
            Assert.That(Guid.TryParse(id, out _), Is.True,
                $"SessionId is not a parseable Guid: {id}");
            Assert.That(id, Has.Length.EqualTo(36),
                $"SessionId is not in canonical 8-4-4-4-12 form: {id}");
            Assert.That(id[14], Is.EqualTo('7'),
                $"Expected version 7 at index 14, got: {id}");
            Assert.That("89ab".IndexOf(id[19]), Is.GreaterThanOrEqualTo(0),
                $"Expected variant 10xx at index 19, got: {id}");
        }

        [Test]
        public void SessionId_UniquePerInstance()
        {
            var a = new Framedash.SessionManager();
            var b = new Framedash.SessionManager();
            Assert.That(a.SessionId, Is.Not.EqualTo(b.SessionId));
        }

        [Test]
        public void DefaultPlayerId_IsEmptyString()
        {
            var mgr = new Framedash.SessionManager();
            Assert.That(mgr.PlayerId, Is.EqualTo(""));
        }

        [Test]
        public void SetPlayerId_UpdatesValue()
        {
            var mgr = new Framedash.SessionManager();
            mgr.SetPlayerId("player-42");
            Assert.That(mgr.PlayerId, Is.EqualTo("player-42"));
        }

        [Test]
        public void SetPlayerId_NullOrWhitespace_RevertsToEmpty()
        {
            var mgr = new Framedash.SessionManager("initial");
            Assert.That(mgr.PlayerId, Is.EqualTo("initial"));

            mgr.SetPlayerId(null);
            Assert.That(mgr.PlayerId, Is.EqualTo(""));

            mgr.SetPlayerId("restored");
            mgr.SetPlayerId("   ");
            Assert.That(mgr.PlayerId, Is.EqualTo(""));
        }

        [Test]
        public void PlayerId_IsTrimmed()
        {
            var mgr = new Framedash.SessionManager("  spaced  ");
            Assert.That(mgr.PlayerId, Is.EqualTo("spaced"));

            mgr.SetPlayerId("  updated  ");
            Assert.That(mgr.PlayerId, Is.EqualTo("updated"));
        }
    }
}
