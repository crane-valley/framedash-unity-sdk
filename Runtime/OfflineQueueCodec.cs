#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace Framedash
{
    internal static class OfflineQueueCodec
    {
        // Changing either identifier discards existing queues, so only incompatible layouts
        // justify a new format version.
        private const int FormatMagic = 0x46445131;
        private const int FormatVersion = 1;
        // Upper bound on any single persisted string's UTF-8 byte length. Generously above
        // the largest field cap (attribute value = 512) so it never rejects a legitimate
        // event, while still bounding the allocation a corrupt length prefix could request.
        private const int MaxPersistedStringBytes = 8192;

        internal static byte[] Serialize(TelemetryEvent[] events)
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

        internal static TelemetryEvent[] Deserialize(byte[] bytes)
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
                if (count < 0 || count > FilePersistence.MaxPersistedEvents)
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
