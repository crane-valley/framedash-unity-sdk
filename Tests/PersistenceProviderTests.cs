using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class PersistenceProviderTests
    {
        private string _dir;
        private string _queuePath;

        [SetUp]
        public void SetUp()
        {
            // Unique temp dir per test so the file-backed cases never collide. The
            // codec/cap logic uses only System.IO, so it runs outside the Unity editor.
            _dir = Path.Combine(Path.GetTempPath(), "framedash-persist-test-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
            _queuePath = Path.Combine(_dir, "offline-queue.bin");
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // Best effort; a leaked temp dir is harmless.
            }
        }

        private static TelemetryEvent MakeEvent(string name, int seed = 0)
        {
            return new TelemetryEvent
            {
                EventName = name,
                TimestampUs = 1_700_000_000_000_000L + seed,
                SessionId = "session-" + seed,
                PlayerId = "player-" + seed,
                PositionX = 1.5f + seed,
                PositionY = 2.5f + seed,
                PositionZ = 3.5f + seed,
                MapId = "map-" + seed,
                Fps = 60f,
                FrameTimeMs = 16.6f,
                MemoryUsedBytes = 123_456_789L + seed,
                GpuTimeMs = 4.2f,
                Source = TelemetrySource.Player,
                BuildId = "build-" + seed,
                Platform = "WindowsPlayer",
                EngineVersion = "2022.3.1f1",
                GameThreadMs = 5.1f,
                RenderThreadMs = 6.2f,
            };
        }

        // -- NullPersistence --

        [Test]
        public void NullPersistence_LoadIsEmpty_MutationsAreNoOps()
        {
            var provider = new NullPersistence();
            Assert.That(provider.Load(), Is.Empty);
            Assert.That(provider.Save(new[] { MakeEvent("a") }), Is.True);
            Assert.That(provider.Append(new[] { MakeEvent("b") }), Is.True);
            Assert.That(provider.DropOldest(1), Is.True);
            Assert.That(provider.Clear(), Is.True);
            // Nothing was ever written.
            Assert.That(provider.Load(), Is.Empty);
        }

        // -- FilePersistence: basic round-trip --

        [Test]
        public void Load_MissingFile_ReturnsEmpty()
        {
            var provider = new FilePersistence(_queuePath);
            Assert.That(provider.Load(), Is.Empty);
            Assert.That(File.Exists(_queuePath), Is.False);
        }

        [Test]
        public void SaveThenLoad_RoundTripsAllFields()
        {
            var evt = MakeEvent("player_death", 7);
            evt.Attributes = new List<StringPair> { new StringPair("weapon", "bow"), new StringPair("zone", "forest") };
            evt.Metrics = new List<FloatPair> { new FloatPair("damage", 42.5f), new FloatPair("score", -1f) };
            evt.CameraYaw = 123.4f;
            evt.CameraPitch = -45.6f;
            evt.Source = TelemetrySource.Automated;

            var provider = new FilePersistence(_queuePath);
            Assert.That(provider.Save(new[] { evt }), Is.True);

            var loaded = provider.Load();
            Assert.That(loaded.Length, Is.EqualTo(1));
            var r = loaded[0];
            Assert.That(r.EventName, Is.EqualTo("player_death"));
            Assert.That(r.TimestampUs, Is.EqualTo(evt.TimestampUs));
            Assert.That(r.SessionId, Is.EqualTo(evt.SessionId));
            Assert.That(r.PlayerId, Is.EqualTo(evt.PlayerId));
            Assert.That(r.PositionX, Is.EqualTo(evt.PositionX));
            Assert.That(r.PositionY, Is.EqualTo(evt.PositionY));
            Assert.That(r.PositionZ, Is.EqualTo(evt.PositionZ));
            Assert.That(r.MapId, Is.EqualTo(evt.MapId));
            Assert.That(r.Fps, Is.EqualTo(evt.Fps));
            Assert.That(r.FrameTimeMs, Is.EqualTo(evt.FrameTimeMs));
            Assert.That(r.MemoryUsedBytes, Is.EqualTo(evt.MemoryUsedBytes));
            Assert.That(r.GpuTimeMs, Is.EqualTo(evt.GpuTimeMs));
            Assert.That(r.Source, Is.EqualTo(TelemetrySource.Automated));
            Assert.That(r.BuildId, Is.EqualTo(evt.BuildId));
            Assert.That(r.Platform, Is.EqualTo(evt.Platform));
            Assert.That(r.EngineVersion, Is.EqualTo(evt.EngineVersion));
            Assert.That(r.GameThreadMs, Is.EqualTo(evt.GameThreadMs));
            Assert.That(r.RenderThreadMs, Is.EqualTo(evt.RenderThreadMs));
            Assert.That(r.CameraYaw, Is.EqualTo(123.4f));
            Assert.That(r.CameraPitch, Is.EqualTo(-45.6f));
            Assert.That(r.Attributes, Is.Not.Null);
            Assert.That(r.Attributes.Count, Is.EqualTo(2));
            Assert.That(r.Attributes[0].Key, Is.EqualTo("weapon"));
            Assert.That(r.Attributes[0].Value, Is.EqualTo("bow"));
            Assert.That(r.Metrics.Count, Is.EqualTo(2));
            Assert.That(r.Metrics[1].Key, Is.EqualTo("score"));
            Assert.That(r.Metrics[1].Value, Is.EqualTo(-1f));
        }

        [Test]
        public void RoundTrip_NullCollectionsAndCamera_StayUnset()
        {
            var evt = MakeEvent("no_extras");
            // Attributes/Metrics null, CameraYaw/Pitch unset.
            var provider = new FilePersistence(_queuePath);
            provider.Save(new[] { evt });

            var r = provider.Load()[0];
            Assert.That(r.Attributes, Is.Null);
            Assert.That(r.Metrics, Is.Null);
            Assert.That(r.CameraYaw.HasValue, Is.False);
            Assert.That(r.CameraPitch.HasValue, Is.False);
        }

        [Test]
        public void Save_EmptyArray_ClearsFile()
        {
            var provider = new FilePersistence(_queuePath);
            provider.Save(new[] { MakeEvent("a") });
            Assert.That(File.Exists(_queuePath), Is.True);

            Assert.That(provider.Save(new TelemetryEvent[0]), Is.True);
            Assert.That(File.Exists(_queuePath), Is.False);
            Assert.That(provider.Load(), Is.Empty);
        }

        [Test]
        public void Save_LeavesNoTempFile()
        {
            var provider = new FilePersistence(_queuePath);
            provider.Save(new[] { MakeEvent("a"), MakeEvent("b", 1) });

            var leftovers = Directory.GetFiles(_dir, "*.tmp");
            Assert.That(leftovers, Is.Empty);
        }

        [Test]
        public void Save_OverExistingFile_ReplacesContent()
        {
            var provider = new FilePersistence(_queuePath);
            provider.Save(new[] { MakeEvent("old", 0), MakeEvent("old2", 1) });
            // Atomic replace path (File.Replace): the second save fully supersedes the first.
            provider.Save(new[] { MakeEvent("new", 2) });

            var loaded = provider.Load();
            Assert.That(loaded.Length, Is.EqualTo(1));
            Assert.That(loaded[0].EventName, Is.EqualTo("new"));
            Assert.That(Directory.GetFiles(_dir, "*.tmp"), Is.Empty);
        }

        // -- FilePersistence: append --

        [Test]
        public void Append_ToEmpty_ThenToExisting_PreservesOrder()
        {
            var provider = new FilePersistence(_queuePath);
            Assert.That(provider.Append(new[] { MakeEvent("a", 0) }), Is.True);
            Assert.That(provider.Append(new[] { MakeEvent("b", 1), MakeEvent("c", 2) }), Is.True);

            var loaded = provider.Load();
            Assert.That(loaded.Length, Is.EqualTo(3));
            Assert.That(loaded[0].EventName, Is.EqualTo("a"));
            Assert.That(loaded[1].EventName, Is.EqualTo("b"));
            Assert.That(loaded[2].EventName, Is.EqualTo("c"));
        }

        [Test]
        public void Append_Empty_IsNoOp()
        {
            var provider = new FilePersistence(_queuePath);
            provider.Append(new[] { MakeEvent("a") });
            Assert.That(provider.Append(new TelemetryEvent[0]), Is.True);
            Assert.That(provider.Append(null), Is.True);
            Assert.That(provider.Load().Length, Is.EqualTo(1));
        }

        [Test]
        public void Append_OverCap_DropsOldest()
        {
            var provider = new FilePersistence(_queuePath);
            int cap = FilePersistence.MaxPersistedEvents;

            var first = new TelemetryEvent[cap];
            for (int i = 0; i < cap; i++) first[i] = MakeEvent("e" + i, i);
            provider.Save(first);

            // Append 5 more -> the 5 oldest are dropped, length stays at the cap.
            var extra = new TelemetryEvent[5];
            for (int i = 0; i < 5; i++) extra[i] = MakeEvent("x" + i, 10000 + i);
            provider.Append(extra);

            var loaded = provider.Load();
            Assert.That(loaded.Length, Is.EqualTo(cap));
            // Oldest 5 (e0..e4) dropped; new head is e5; tail is the appended x4.
            Assert.That(loaded[0].EventName, Is.EqualTo("e5"));
            Assert.That(loaded[cap - 1].EventName, Is.EqualTo("x4"));
        }

        // -- FilePersistence: drop / clear --

        [Test]
        public void DropOldest_RemovesLeadingN()
        {
            var provider = new FilePersistence(_queuePath);
            provider.Save(new[] { MakeEvent("a", 0), MakeEvent("b", 1), MakeEvent("c", 2) });

            Assert.That(provider.DropOldest(2), Is.True);
            var loaded = provider.Load();
            Assert.That(loaded.Length, Is.EqualTo(1));
            Assert.That(loaded[0].EventName, Is.EqualTo("c"));
        }

        [Test]
        public void DropOldest_AtOrAboveCount_ClearsFile()
        {
            var provider = new FilePersistence(_queuePath);
            provider.Save(new[] { MakeEvent("a"), MakeEvent("b", 1) });

            Assert.That(provider.DropOldest(2), Is.True);
            Assert.That(File.Exists(_queuePath), Is.False);
            Assert.That(provider.Load(), Is.Empty);
        }

        [Test]
        public void DropOldest_NonPositive_IsNoOp()
        {
            var provider = new FilePersistence(_queuePath);
            provider.Save(new[] { MakeEvent("a") });
            Assert.That(provider.DropOldest(0), Is.True);
            Assert.That(provider.DropOldest(-3), Is.True);
            Assert.That(provider.Load().Length, Is.EqualTo(1));
        }

        [Test]
        public void Clear_RemovesFile()
        {
            var provider = new FilePersistence(_queuePath);
            provider.Save(new[] { MakeEvent("a") });
            Assert.That(provider.Clear(), Is.True);
            Assert.That(File.Exists(_queuePath), Is.False);
            Assert.That(provider.Load(), Is.Empty);
        }

        // -- FilePersistence: corruption / incompatibility --

        [Test]
        public void Load_CorruptFile_ReturnsEmptyAndDeletes()
        {
            File.WriteAllBytes(_queuePath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 });
            var provider = new FilePersistence(_queuePath);

            Assert.That(provider.Load(), Is.Empty);
            // The unreadable file is discarded so it cannot wedge every future load.
            Assert.That(File.Exists(_queuePath), Is.False);
        }

        [Test]
        public void Load_TruncatedFile_ReturnsEmptyAndDeletes()
        {
            var provider = new FilePersistence(_queuePath);
            provider.Save(new[] { MakeEvent("a"), MakeEvent("b", 1) });
            byte[] full = File.ReadAllBytes(_queuePath);

            // Keep the valid 12-byte header (magic/version/count=2) plus a few bytes of the
            // first event, then cut the stream off so reading the events hits EndOfStream
            // (the truncated-stream path, distinct from a magic/version mismatch).
            Assert.That(full.Length, Is.GreaterThan(16));
            var truncated = new byte[15];
            System.Array.Copy(full, truncated, truncated.Length);
            File.WriteAllBytes(_queuePath, truncated);

            Assert.That(provider.Load(), Is.Empty);
            Assert.That(File.Exists(_queuePath), Is.False);
        }

        [Test]
        public void Load_ImplausibleEventCount_ReturnsEmptyAndDeletes()
        {
            // Valid magic + version, but a count far beyond MaxPersistedEvents. The
            // reader must reject it before allocating an array of that size.
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0x46445131); // FormatMagic ("FDQ1")
                w.Write(1);          // FormatVersion
                w.Write(int.MaxValue); // absurd event count
                w.Flush();
                File.WriteAllBytes(_queuePath, ms.ToArray());
            }

            var provider = new FilePersistence(_queuePath);
            Assert.That(provider.Load(), Is.Empty);
            Assert.That(File.Exists(_queuePath), Is.False);
        }

        [Test]
        public void Load_OversizedStringLength_ReturnsEmptyAndDeletes()
        {
            // Valid magic/version/count, but the first event's EventName claims a UTF-8
            // length far beyond MaxPersistedStringBytes. The bounded reader must reject it
            // before allocating, then the catch path discards the file.
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0x46445131); // FormatMagic
                w.Write(1);          // FormatVersion
                w.Write(1);          // count = 1 event
                // 7-bit-encoded length 100000 (> 8192) for the first string (EventName).
                w.Write((byte)0xA0);
                w.Write((byte)0x8D);
                w.Write((byte)0x06);
                w.Flush();
                File.WriteAllBytes(_queuePath, ms.ToArray());
            }

            var provider = new FilePersistence(_queuePath);
            Assert.That(provider.Load(), Is.Empty);
            Assert.That(File.Exists(_queuePath), Is.False);
        }

        [Test]
        public void Reload_NewInstanceSameFile_SeesPersistedEvents()
        {
            new FilePersistence(_queuePath).Append(new[] { MakeEvent("a", 0), MakeEvent("b", 1) });
            // A fresh instance (mirrors a Shutdown-then-Initialize) reads the same file.
            var reopened = new FilePersistence(_queuePath);
            Assert.That(reopened.Load().Length, Is.EqualTo(2));
        }
    }
}
