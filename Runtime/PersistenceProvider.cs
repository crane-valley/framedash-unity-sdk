#nullable enable

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
        TelemetryEvent[] Load();

        bool Save(TelemetryEvent[]? events);

        bool Append(TelemetryEvent[]? events);

        bool DropOldest(int count);

        bool Clear();
    }

    public sealed class NullPersistence : IPersistenceProvider
    {
        public TelemetryEvent[] Load() => Array.Empty<TelemetryEvent>();
        public bool Save(TelemetryEvent[]? events) => true;
        public bool Append(TelemetryEvent[]? events) => true;
        public bool DropOldest(int count) => true;
        public bool Clear() => true;
    }

    /// <summary>
    /// File-backed persistence provider. The queue is stored as a single
    /// hand-written binary file (no JSON/codegen dependency, matching the SDK's
    /// hand-written Protobuf style) and rewritten atomically (temp file + replace)
    /// on every mutation. Engine-independent apart from <see cref="DefaultQueueFilePath"/>:
    /// the path-injecting constructor uses only System.IO, so it is unit-tested
    /// under NextUnit with a temp path.
    /// </summary>
    public sealed class FilePersistence : IPersistenceProvider
    {
        /// <summary>
        /// Hard cap on the persisted queue length. Matches the UE5 SDK
        /// (FramedashConstants::MaxPersistedEvents). Appending beyond this drops the
        /// oldest events so the on-disk queue cannot grow without bound.
        /// </summary>
        public const int MaxPersistedEvents = 1000;

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
        public static string DefaultQueueFilePath(string? configDiscriminator)
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

        public bool Save(TelemetryEvent[]? events)
        {
            lock (_fileLock)
            {
                return SaveToDisk(events);
            }
        }

        public bool Append(TelemetryEvent[]? events)
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


        private bool SaveToDisk(TelemetryEvent[]? events)
        {
            if (events == null || events.Length == 0) return ClearFromDisk();

            // Declared outside the try so the catch can clean it up if the swap fails.
            string tempPath = _queueFilePath + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
            try
            {
                string? directory = Path.GetDirectoryName(_queueFilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                byte[] bytes = OfflineQueueCodec.Serialize(events);

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
                catch {   }
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
                return OfflineQueueCodec.Deserialize(bytes);
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
            }
        }

    }
}
