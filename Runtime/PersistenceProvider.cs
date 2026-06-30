using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Framedash
{
    /// <summary>
    /// Stores unsent telemetry so app shutdowns and transient network failures do
    /// not lose the whole in-memory queue. Implementations must stay fail-safe:
    /// every method swallows its own I/O errors and returns a status instead of
    /// throwing, so a disk problem can never disrupt the game.
    /// </summary>
    public interface IPersistenceProvider
    {
        /// <summary>Load the persisted queue (oldest first). Returns an empty array on any error.</summary>
        TelemetryEvent[] Load();

        /// <summary>Overwrite the persisted queue with exactly these events. Returns false on failure.</summary>
        bool Save(TelemetryEvent[] events);

        /// <summary>Append events to the tail of the persisted queue (capped at the max). Returns false on failure.</summary>
        bool Append(TelemetryEvent[] events);

        /// <summary>Drop the oldest <paramref name="count"/> events from the head of the queue. Returns false on failure.</summary>
        bool DropOldest(int count);

        /// <summary>Remove the persisted queue entirely. Returns false on failure.</summary>
        bool Clear();
    }

    /// <summary>No-op persistence provider for projects that explicitly disable the offline queue.</summary>
    public sealed class NullPersistence : IPersistenceProvider
    {
        public TelemetryEvent[] Load() => Array.Empty<TelemetryEvent>();
        public bool Save(TelemetryEvent[] events) => true;
        public bool Append(TelemetryEvent[] events) => true;
        public bool DropOldest(int count) => true;
        public bool Clear() => true;
    }

    /// <summary>
    /// File-backed persistence provider. The queue is stored as a single
    /// hand-written binary file (no JSON/codegen dependency, matching the SDK's
    /// hand-written Protobuf style) and rewritten atomically (temp file + replace)
    /// on every mutation. Engine-independent apart from <see cref="DefaultQueueFilePath"/>:
    /// the path-injecting constructor uses only System.IO, so it is unit-tested
    /// under NUnit with a temp path.
    /// </summary>
    public sealed class FilePersistence : IPersistenceProvider
    {
        /// <summary>
        /// Hard cap on the persisted queue length. Matches the UE5 SDK
        /// (FramedashConstants::MaxPersistedEvents). Appending beyond this drops the
        /// oldest events so the on-disk queue cannot grow without bound.
        /// </summary>
        public const int MaxPersistedEvents = 1000;

        // Bumped only on an incompatible on-disk layout change. A file whose magic
        // or version does not match is treated as unreadable and discarded.
        private const int FormatMagic = 0x46445131; // "FDQ1"
        private const int FormatVersion = 1;
        // Upper bound on any single persisted string's UTF-8 byte length. Generously above
        // the largest field cap (attribute value = 512) so it never rejects a legitimate
        // event, while still bounding the allocation a corrupt length prefix could request.
        private const int MaxPersistedStringBytes = 8192;

        // Serializes concurrent file access. Append/DropOldest are read-modify-write, so
        // the whole sequence must hold the lock to avoid interleaved rewrites. Static so
        // it protects the FILE, not a single instance: a Shutdown-then-Initialize creates
        // a new FilePersistence over the same queue file while a prior flush may still be
        // writing to it, and both must serialize (matches the UE5 SDK's file-level lock).
        private static readonly object _fileLock = new object();
        private readonly string _queueFilePath;

        public FilePersistence(string queueFilePath)
        {
            _queueFilePath = queueFilePath;
        }

        /// <summary>
        /// Default queue location under Unity's per-user persistent data directory,
        /// partitioned by a stable hash of the ingest configuration (endpoint + API key)
        /// so switching project/environment between runs cannot resend a prior
        /// configuration's events to a new one. This is the only engine-coupled member;
        /// kept out of the constructor so the rest of the class is engine-independent and
        /// unit-testable.
        /// </summary>
        public static string DefaultQueueFilePath(string configDiscriminator)
        {
            string hash = StableHashHex(configDiscriminator ?? "");
            return Path.Combine(Application.persistentDataPath, "Framedash", $"offline-queue-{hash}.bin");
        }

        // FNV-1a 32-bit over UTF-8: a stable (cross-run, cross-platform) non-cryptographic
        // hash for the queue filename. Not security-sensitive -- it only partitions queue
        // files by configuration; the API key is never written, only mixed into the hash.
        private static string StableHashHex(string value)
        {
            const uint offsetBasis = 2166136261u;
            const uint prime = 16777619u;
            uint hash = offsetBasis;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(value))
            {
                hash ^= b;
                hash *= prime;
            }
            return hash.ToString("x8");
        }

        public TelemetryEvent[] Load()
        {
            lock (_fileLock)
            {
                return LoadFromDisk();
            }
        }

        public bool Save(TelemetryEvent[] events)
        {
            lock (_fileLock)
            {
                return SaveToDisk(events);
            }
        }

        public bool Append(TelemetryEvent[] events)
        {
            if (events == null || events.Length == 0) return true;

            lock (_fileLock)
            {
                var existing = new List<TelemetryEvent>(LoadFromDisk());
                existing.AddRange(events);

                int overflow = existing.Count - MaxPersistedEvents;
                if (overflow > 0)
                {
                    existing.RemoveRange(0, overflow);
                    Debug.LogWarning($"[Framedash] Offline queue full. Dropped {overflow} oldest persisted event(s).");
                }

                return SaveToDisk(existing.ToArray());
            }
        }

        public bool DropOldest(int count)
        {
            if (count <= 0) return true;

            lock (_fileLock)
            {
                var existing = LoadFromDisk();
                if (existing.Length == 0) return true;
                if (count >= existing.Length) return ClearFromDisk();

                var remaining = new TelemetryEvent[existing.Length - count];
                Array.Copy(existing, count, remaining, 0, remaining.Length);
                return SaveToDisk(remaining);
            }
        }

        public bool Clear()
        {
            lock (_fileLock)
            {
                return ClearFromDisk();
            }
        }

        // ---- disk I/O (caller holds _fileLock) ----

        private bool SaveToDisk(TelemetryEvent[] events)
        {
            if (events == null || events.Length == 0) return ClearFromDisk();

            // Declared outside the try so the catch can clean it up if the swap fails.
            string tempPath = _queueFilePath + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(_queueFilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                byte[] bytes = Serialize(events);

                // Write to a temp file then swap it in atomically, so a crash at any
                // point keeps either the previous good queue or the new one -- never a
                // half-written or missing file. File.Replace is the atomic swap when a
                // queue already exists; a plain Move covers the first write (nothing to
                // lose yet). Deleting the old file before the move would open a window
                // where a crash loses the only good copy.
                File.WriteAllBytes(tempPath, bytes);
                if (File.Exists(_queueFilePath))
                    File.Replace(tempPath, _queueFilePath, destinationBackupFileName: null);
                else
                    File.Move(tempPath, _queueFilePath);
                return true;
            }
            catch (Exception e)
            {
                // Best-effort: remove the temp file so a failed swap does not leak one
                // per attempt across runs.
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { /* nothing more we can safely do */ }
                Debug.LogWarning($"[Framedash] Failed to write offline queue: {e.Message}");
                return false;
            }
        }

        private bool ClearFromDisk()
        {
            try
            {
                if (File.Exists(_queueFilePath)) File.Delete(_queueFilePath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Framedash] Failed to clear offline queue: {e.Message}");
                return false;
            }
        }

        private TelemetryEvent[] LoadFromDisk()
        {
            if (!File.Exists(_queueFilePath)) return Array.Empty<TelemetryEvent>();

            try
            {
                byte[] bytes = File.ReadAllBytes(_queueFilePath);
                return Deserialize(bytes);
            }
            catch (Exception e)
            {
                // A corrupt/incompatible file is never the game's problem: discard it
                // so it cannot wedge every future load, and start with an empty queue.
                Debug.LogWarning($"[Framedash] Ignoring unreadable offline queue: {e.Message}");
                TryDelete();
                return Array.Empty<TelemetryEvent>();
            }
        }

        private void TryDelete()
        {
            try
            {
                if (File.Exists(_queueFilePath)) File.Delete(_queueFilePath);
            }
            catch
            {
                // Best effort; nothing more we can safely do here.
            }
        }

        // ---- binary codec ----

        private static byte[] Serialize(TelemetryEvent[] events)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(FormatMagic);
                writer.Write(FormatVersion);
                writer.Write(events.Length);
                foreach (var evt in events) WriteEvent(writer, evt);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static TelemetryEvent[] Deserialize(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, writable: false))
            using (var reader = new BinaryReader(stream))
            {
                int magic = reader.ReadInt32();
                int version = reader.ReadInt32();
                if (magic != FormatMagic || version != FormatVersion)
                {
                    throw new InvalidDataException(
                        $"offline queue magic/version mismatch (magic=0x{magic:X8}, version={version})");
                }

                int count = reader.ReadInt32();
                // Reject an implausible count before allocating: a legitimate queue is
                // capped at MaxPersistedEvents, so anything larger (or negative) is a
                // corrupt file. Without this, a corrupt count could request a huge array.
                if (count < 0 || count > MaxPersistedEvents)
                    throw new InvalidDataException($"offline queue invalid count ({count})");

                var events = new TelemetryEvent[count];
                for (int i = 0; i < count; i++) events[i] = ReadEvent(reader);
                return events;
            }
        }

        private static void WriteEvent(BinaryWriter writer, TelemetryEvent evt)
        {
            writer.Write(evt.EventName ?? "");
            writer.Write(evt.TimestampUs);
            writer.Write(evt.SessionId ?? "");
            writer.Write(evt.PlayerId ?? "");
            writer.Write(evt.PositionX);
            writer.Write(evt.PositionY);
            writer.Write(evt.PositionZ);
            writer.Write(evt.MapId ?? "");
            writer.Write(evt.Fps);
            writer.Write(evt.FrameTimeMs);
            writer.Write(evt.MemoryUsedBytes);
            writer.Write(evt.GpuTimeMs);
            writer.Write((int)evt.Source);
            writer.Write(evt.BuildId ?? "");
            writer.Write(evt.Platform ?? "");
            writer.Write(evt.EngineVersion ?? "");

            int attrCount = evt.Attributes?.Count ?? 0;
            writer.Write(attrCount);
            if (evt.Attributes != null)
            {
                foreach (var pair in evt.Attributes)
                {
                    writer.Write(pair.Key ?? "");
                    writer.Write(pair.Value ?? "");
                }
            }

            int metricCount = evt.Metrics?.Count ?? 0;
            writer.Write(metricCount);
            if (evt.Metrics != null)
            {
                foreach (var pair in evt.Metrics)
                {
                    writer.Write(pair.Key ?? "");
                    writer.Write(pair.Value);
                }
            }

            writer.Write(evt.GameThreadMs);
            writer.Write(evt.RenderThreadMs);
            WriteOptionalFloat(writer, evt.CameraYaw);
            WriteOptionalFloat(writer, evt.CameraPitch);
        }

        private static TelemetryEvent ReadEvent(BinaryReader reader)
        {
            var evt = new TelemetryEvent
            {
                EventName = ReadBoundedString(reader),
                TimestampUs = reader.ReadInt64(),
                SessionId = ReadBoundedString(reader),
                PlayerId = ReadBoundedString(reader),
                PositionX = reader.ReadSingle(),
                PositionY = reader.ReadSingle(),
                PositionZ = reader.ReadSingle(),
                MapId = ReadBoundedString(reader),
                Fps = reader.ReadSingle(),
                FrameTimeMs = reader.ReadSingle(),
                MemoryUsedBytes = reader.ReadInt64(),
                GpuTimeMs = reader.ReadSingle(),
                Source = (TelemetrySource)reader.ReadInt32(),
                BuildId = ReadBoundedString(reader),
                Platform = ReadBoundedString(reader),
                EngineVersion = ReadBoundedString(reader),
            };

            int attrCount = reader.ReadInt32();
            // Runtime events are clamped to the ingest caps before persisting, so a count
            // beyond the cap (or negative) means a corrupt file -- reject before allocating.
            if (attrCount < 0 || attrCount > FieldClamp.MaxAttributes)
                throw new InvalidDataException($"offline queue invalid attribute count ({attrCount})");
            if (attrCount > 0)
            {
                evt.Attributes = new List<StringPair>(attrCount);
                for (int i = 0; i < attrCount; i++)
                {
                    string key = ReadBoundedString(reader);
                    string value = ReadBoundedString(reader);
                    evt.Attributes.Add(new StringPair(key, value));
                }
            }

            int metricCount = reader.ReadInt32();
            if (metricCount < 0 || metricCount > FieldClamp.MaxMetrics)
                throw new InvalidDataException($"offline queue invalid metric count ({metricCount})");
            if (metricCount > 0)
            {
                evt.Metrics = new List<FloatPair>(metricCount);
                for (int i = 0; i < metricCount; i++)
                {
                    string key = ReadBoundedString(reader);
                    float value = reader.ReadSingle();
                    evt.Metrics.Add(new FloatPair(key, value));
                }
            }

            evt.GameThreadMs = reader.ReadSingle();
            evt.RenderThreadMs = reader.ReadSingle();
            evt.CameraYaw = ReadOptionalFloat(reader);
            evt.CameraPitch = ReadOptionalFloat(reader);
            return evt;
        }

        // Read a string written by BinaryWriter.Write(string) (a 7-bit-encoded UTF-8 byte
        // length followed by the bytes), but validate the length against a cap first so a
        // corrupt length prefix cannot drive a huge allocation before the read fails.
        private static string ReadBoundedString(BinaryReader reader)
        {
            int byteLength = Read7BitEncodedLength(reader);
            if (byteLength < 0 || byteLength > MaxPersistedStringBytes)
                throw new InvalidDataException($"offline queue string too long ({byteLength} bytes)");
            if (byteLength == 0) return "";

            byte[] bytes = reader.ReadBytes(byteLength);
            if (bytes.Length != byteLength) throw new EndOfStreamException("offline queue truncated string");
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        // Mirrors BinaryWriter's 7-bit-encoded length prefix. Implemented here (rather than
        // BinaryReader.Read7BitEncodedInt, which is not public on all Unity runtimes) and
        // bounded to 5 bytes so a corrupt stream of high-bit bytes cannot loop unbounded.
        private static int Read7BitEncodedLength(BinaryReader reader)
        {
            int value = 0;
            int shift = 0;
            byte current;
            do
            {
                if (shift >= 35) throw new InvalidDataException("offline queue malformed string length");
                current = reader.ReadByte();
                value |= (current & 0x7F) << shift;
                shift += 7;
            } while ((current & 0x80) != 0);
            return value;
        }

        private static void WriteOptionalFloat(BinaryWriter writer, float? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue) writer.Write(value.Value);
        }

        private static float? ReadOptionalFloat(BinaryReader reader)
        {
            bool hasValue = reader.ReadBoolean();
            return hasValue ? reader.ReadSingle() : (float?)null;
        }
    }
}
