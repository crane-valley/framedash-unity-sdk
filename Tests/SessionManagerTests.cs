using System;
using System.Collections.Generic;
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

        [Test]
        public void PlayerId_OverLimit_TruncatedTo128()
        {
            // An over-limit player_id is rejected by ingest validation (dropping the
            // whole batch), so the SDK truncates to MAX_PLAYER_ID_LEN (128).
            string longId = new string('p', 200);
            var mgr = new Framedash.SessionManager(longId);
            Assert.That(mgr.PlayerId, Has.Length.EqualTo(128));

            mgr.SetPlayerId(new string('q', 150));
            Assert.That(mgr.PlayerId, Has.Length.EqualTo(128));
        }

        // --- Automated-session attributes (BeginAutomatedSession metadata contract) ---

        [Test]
        public void SessionAttributes_DefaultNone_MergeReturnsEventListUnchanged()
        {
            var mgr = new SessionManager();
            Assert.That(mgr.HasSessionAttributes, Is.False);

            var perEvent = new List<StringPair> { new StringPair("a", "1") };
            // No session attributes -> the per-event list is returned as-is (same reference).
            Assert.That(mgr.MergeWithSessionAttributes(perEvent), Is.SameAs(perEvent));
            Assert.That(mgr.MergeWithSessionAttributes(null), Is.Null);
        }

        [Test]
        public void SetSessionAttributes_MergesOntoAttributeFreeEvent()
        {
            var mgr = new SessionManager();
            mgr.SetSessionAttributes(new Dictionary<string, string>
            {
                ["ci.branch"] = "main",
                ["ci.commit"] = "abc123",
            });
            Assert.That(mgr.HasSessionAttributes, Is.True);

            // No per-event attributes -> the shared session list is returned (no allocation),
            // so an attribute-free event (e.g. perf_heartbeat) still carries the CI metadata.
            var merged = mgr.MergeWithSessionAttributes(null);
            Assert.That(merged, Has.Count.EqualTo(2));
            Assert.That(ValueFor(merged, "ci.branch"), Is.EqualTo("main"));
            Assert.That(ValueFor(merged, "ci.commit"), Is.EqualTo("abc123"));
            // An empty (non-null) per-event list behaves the same as null.
            Assert.That(mgr.MergeWithSessionAttributes(new List<StringPair>()), Has.Count.EqualTo(2));
        }

        [Test]
        public void MergeWithSessionAttributes_CombinesDistinctKeys_SessionFirst()
        {
            var mgr = new SessionManager();
            mgr.SetSessionAttributes(new Dictionary<string, string> { ["ci.branch"] = "main" });

            var perEvent = new List<StringPair> { new StringPair("weapon", "bow") };
            var merged = mgr.MergeWithSessionAttributes(perEvent);

            Assert.That(merged, Has.Count.EqualTo(2));
            Assert.That(merged[0].Key, Is.EqualTo("ci.branch"), "session entry comes first");
            Assert.That(ValueFor(merged, "ci.branch"), Is.EqualTo("main"));
            Assert.That(ValueFor(merged, "weapon"), Is.EqualTo("bow"));
            // The input list is not mutated.
            Assert.That(perEvent, Has.Count.EqualTo(1));
        }

        [Test]
        public void MergeWithSessionAttributes_PerEventOverridesSessionKey()
        {
            var mgr = new SessionManager();
            mgr.SetSessionAttributes(new Dictionary<string, string>
            {
                ["ci.branch"] = "main",
                ["ci.commit"] = "abc",
            });

            var perEvent = new List<StringPair> { new StringPair("ci.branch", "feature") };
            var merged = mgr.MergeWithSessionAttributes(perEvent);

            Assert.That(merged, Has.Count.EqualTo(2), "an override does not add a duplicate key");
            Assert.That(ValueFor(merged, "ci.branch"), Is.EqualTo("feature"));
            Assert.That(ValueFor(merged, "ci.commit"), Is.EqualTo("abc"));
        }

        [Test]
        public void SetSessionAttributes_NullEmptyOrClear_RemovesTagging()
        {
            var mgr = new SessionManager();
            mgr.SetSessionAttributes(new Dictionary<string, string> { ["ci.branch"] = "main" });
            Assert.That(mgr.HasSessionAttributes, Is.True);

            mgr.SetSessionAttributes(null);
            Assert.That(mgr.HasSessionAttributes, Is.False);

            mgr.SetSessionAttributes(new Dictionary<string, string> { ["ci.branch"] = "main" });
            mgr.ClearSessionAttributes();
            Assert.That(mgr.HasSessionAttributes, Is.False);

            // An empty dictionary clears rather than setting an empty session.
            mgr.SetSessionAttributes(new Dictionary<string, string>());
            Assert.That(mgr.HasSessionAttributes, Is.False);
        }

        [Test]
        public void SetSessionAttributes_ClampsOverCapValue()
        {
            var mgr = new SessionManager();
            mgr.SetSessionAttributes(new Dictionary<string, string>
            {
                ["ci.scenario"] = new string('s', FieldClamp.MaxAttributeValueLength + 50),
            });

            var merged = mgr.MergeWithSessionAttributes(null);
            Assert.That(ValueFor(merged, "ci.scenario"),
                Has.Length.EqualTo(FieldClamp.MaxAttributeValueLength));
        }

        [Test]
        public void MergeWithSessionAttributes_CapsCombinedAtMax_KeepsSession()
        {
            var mgr = new SessionManager();
            mgr.SetSessionAttributes(new Dictionary<string, string> { ["ci.branch"] = "main" });

            // 50 distinct per-event keys + 1 session key = 51 -> capped to 50 with the
            // session entry retained (it is placed first).
            var perEvent = new List<StringPair>();
            for (int i = 0; i < FieldClamp.MaxAttributes; i++)
                perEvent.Add(new StringPair($"k{i}", "v"));

            var merged = mgr.MergeWithSessionAttributes(perEvent);
            Assert.That(merged, Has.Count.EqualTo(FieldClamp.MaxAttributes));
            Assert.That(merged[0].Key, Is.EqualTo("ci.branch"), "session attribute survives the cap");
            Assert.That(ValueFor(merged, "ci.branch"), Is.EqualTo("main"));
        }

        [Test]
        public void MergeWithSessionAttributes_OverrideSurvivesAtCap()
        {
            var mgr = new SessionManager();
            mgr.SetSessionAttributes(new Dictionary<string, string> { ["ci.branch"] = "main" });

            // 49 new per-event keys + an override of the session key = 50 inputs; the
            // override must win and the result stays within the cap (the contract holds
            // even at the cap boundary).
            var perEvent = new List<StringPair> { new StringPair("ci.branch", "feature") };
            for (int i = 0; i < FieldClamp.MaxAttributes - 1; i++)
                perEvent.Add(new StringPair($"k{i}", "v"));

            var merged = mgr.MergeWithSessionAttributes(perEvent);
            Assert.That(merged, Has.Count.EqualTo(FieldClamp.MaxAttributes));
            Assert.That(ValueFor(merged, "ci.branch"), Is.EqualTo("feature"),
                "the per-event override wins even when the per-event set fills the cap");
        }

        // --- ResolveSessionStamp (build_id override + attributes from one snapshot) ---

        [Test]
        public void ResolveSessionStamp_NoSession_ReturnsFallbackAndEventAttrs()
        {
            var mgr = new SessionManager();
            var perEvent = new List<StringPair> { new StringPair("a", "1") };
            var stamp = mgr.ResolveSessionStamp("configured-build", perEvent);
            Assert.That(stamp.BuildId, Is.EqualTo("configured-build"));
            // No session -> the per-event list is returned as-is (same reference).
            Assert.That(stamp.Attributes, Is.SameAs(perEvent));
        }

        [Test]
        public void ResolveSessionStamp_SessionBuildId_OverridesFallbackAndMerges()
        {
            var mgr = new SessionManager();
            mgr.SetAutomatedSession("candidate-build",
                new Dictionary<string, string> { ["ci.branch"] = "main" });

            var stamp = mgr.ResolveSessionStamp("configured-build", null);
            Assert.That(stamp.BuildId, Is.EqualTo("candidate-build"), "session build_id wins");
            Assert.That(ValueFor(stamp.Attributes, "ci.branch"), Is.EqualTo("main"));
        }

        [Test]
        public void ResolveSessionStamp_SessionWithoutBuildId_KeepsFallback()
        {
            var mgr = new SessionManager();
            mgr.SetAutomatedSession(null,
                new Dictionary<string, string> { ["ci.scenario"] = "boss" });

            var stamp = mgr.ResolveSessionStamp("configured-build", null);
            Assert.That(stamp.BuildId, Is.EqualTo("configured-build"), "no override -> fallback build_id");
            Assert.That(ValueFor(stamp.Attributes, "ci.scenario"), Is.EqualTo("boss"));
        }

        [Test]
        public void SetAutomatedSession_BuildIdOnly_NoAttributeTagging()
        {
            var mgr = new SessionManager();
            mgr.SetAutomatedSession("candidate-build", null);
            Assert.That(mgr.HasSessionAttributes, Is.False, "a build_id override sets no ci.* tags");

            var stamp = mgr.ResolveSessionStamp("configured-build", null);
            Assert.That(stamp.BuildId, Is.EqualTo("candidate-build"));
            Assert.That(stamp.Attributes, Is.Null, "no per-event and no session attrs -> null");
        }

        [Test]
        public void ClearSessionAttributes_DropsBuildIdOverrideToo()
        {
            var mgr = new SessionManager();
            mgr.SetAutomatedSession("candidate-build",
                new Dictionary<string, string> { ["ci.branch"] = "main" });

            mgr.ClearSessionAttributes();

            var stamp = mgr.ResolveSessionStamp("configured-build", null);
            Assert.That(stamp.BuildId, Is.EqualTo("configured-build"), "End drops the build_id override");
            Assert.That(mgr.HasSessionAttributes, Is.False);
        }

        private static string ValueFor(List<StringPair> list, string key)
        {
            foreach (var p in list)
                if (p.Key == key) return p.Value;
            return null;
        }
    }
}
