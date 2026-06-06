using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Framedash.Tests
{
    [TestFixture]
    public class TelemetrySerializerTests
    {
        /// <summary>
        /// Helper: decode a varint starting at offset, return value and new offset.
        /// </summary>
        private static (ulong value, int newOffset) ReadVarint(byte[] data, int offset)
        {
            ulong result = 0;
            int shift = 0;
            while (offset < data.Length)
            {
                byte b = data[offset++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return (result, offset);
        }

        /// <summary>
        /// Helper: read a length-delimited field's bytes starting after the tag.
        /// </summary>
        private static (byte[] payload, int newOffset) ReadLengthDelimited(byte[] data, int offset)
        {
            var (length, off) = ReadVarint(data, offset);
            int len = (int)length;
            var payload = new byte[len];
            Array.Copy(data, off, payload, 0, len);
            return (payload, off + len);
        }

        /// <summary>
        /// Walk proto wire format and collect (fieldNumber, wireType, rawOffset) tuples.
        /// </summary>
        private static List<(int field, int wireType, int valueOffset)> ParseFields(byte[] data)
        {
            var fields = new List<(int, int, int)>();
            int offset = 0;
            while (offset < data.Length)
            {
                var (tag, off) = ReadVarint(data, offset);
                int fieldNumber = (int)(tag >> 3);
                int wireType = (int)(tag & 0x07);
                fields.Add((fieldNumber, wireType, off));

                // Advance past the value
                switch (wireType)
                {
                    case 0: // varint
                        while (off < data.Length && (data[off++] & 0x80) != 0) { }
                        break;
                    case 1: // 64-bit
                        off += 8;
                        break;
                    case 2: // length-delimited
                        var (len, newOff) = ReadVarint(data, off);
                        off = newOff + (int)len;
                        break;
                    case 5: // 32-bit
                        off += 4;
                        break;
                    default:
                        throw new Exception($"Unknown wire type {wireType}");
                }
                offset = off;
            }
            return fields;
        }

        [Test]
        public void Serialize_SingleEvent_AllFieldsPresent()
        {
            var events = new[]
            {
                new TelemetryEvent
                {
                    EventName = "test_event",
                    TimestampUs = 1700000000000000L,
                    SessionId = "sess-1",
                    PlayerId = "player-1",
                    PositionX = 1.0f,
                    PositionY = 2.0f,
                    PositionZ = 3.0f,
                    MapId = "map-1",
                    Fps = 60.0f,
                    FrameTimeMs = 16.67f,
                    MemoryUsedBytes = 1024 * 1024L,
                    GpuTimeMs = 8.5f,
                    Source = TelemetrySource.Player,
                    BuildId = "build-1",
                    Platform = "Windows",
                    EngineVersion = "2022.3",
                    GameThreadMs = 12.3f,
                    RenderThreadMs = 4.5f,
                }
            };

            var bytes = TelemetrySerializer.Serialize(events);
            Assert.That(bytes, Is.Not.Empty);

            // Outer message: TelemetryBatch with field 1 (repeated events)
            var batchFields = ParseFields(bytes);
            Assert.That(batchFields, Has.Count.EqualTo(1));
            Assert.That(batchFields[0].field, Is.EqualTo(1)); // events field
            Assert.That(batchFields[0].wireType, Is.EqualTo(2)); // length-delimited

            // Decode the inner event message
            var (eventPayload, _) = ReadLengthDelimited(bytes, batchFields[0].valueOffset);
            var eventFields = ParseFields(eventPayload);

            // Verify all expected field numbers are present
            var fieldNumbers = new HashSet<int>();
            foreach (var f in eventFields)
                fieldNumbers.Add(f.field);

            // Required non-zero fields from our test event
            Assert.That(fieldNumbers, Does.Contain(1), "event_name");
            Assert.That(fieldNumbers, Does.Contain(2), "timestamp_us");
            Assert.That(fieldNumbers, Does.Contain(3), "session_id");
            Assert.That(fieldNumbers, Does.Contain(4), "player_id");
            Assert.That(fieldNumbers, Does.Contain(5), "position (sub-message)");
            Assert.That(fieldNumbers, Does.Contain(6), "map_id");
            Assert.That(fieldNumbers, Does.Not.Contain(7), "field 7 reserved (was zone_id)");
            Assert.That(fieldNumbers, Does.Contain(8), "fps");
            Assert.That(fieldNumbers, Does.Contain(9), "frame_time_ms");
            Assert.That(fieldNumbers, Does.Contain(10), "memory_used_bytes");
            Assert.That(fieldNumbers, Does.Contain(11), "gpu_time_ms");
            Assert.That(fieldNumbers, Does.Contain(14), "source enum");
            Assert.That(fieldNumbers, Does.Contain(15), "build_id");
            Assert.That(fieldNumbers, Does.Contain(16), "platform");
            Assert.That(fieldNumbers, Does.Contain(17), "engine_version");
            Assert.That(fieldNumbers, Does.Contain(20), "game_thread_ms");
            Assert.That(fieldNumbers, Does.Contain(21), "render_thread_ms");
        }

        [Test]
        public void Serialize_Proto3DefaultSkipping_OmitsZeroAndEmpty()
        {
            var events = new[]
            {
                new TelemetryEvent
                {
                    EventName = "minimal",
                    TimestampUs = 100L,
                    SessionId = "s",
                    // All other fields default to zero/null/empty
                }
            };

            var bytes = TelemetrySerializer.Serialize(events);
            var batchFields = ParseFields(bytes);
            var (eventPayload, _) = ReadLengthDelimited(bytes, batchFields[0].valueOffset);
            var eventFields = ParseFields(eventPayload);

            var fieldNumbers = new HashSet<int>();
            foreach (var f in eventFields)
                fieldNumbers.Add(f.field);

            // These should be present (non-default)
            Assert.That(fieldNumbers, Does.Contain(1), "event_name");
            Assert.That(fieldNumbers, Does.Contain(2), "timestamp_us");
            Assert.That(fieldNumbers, Does.Contain(3), "session_id");

            // These should be omitted (zero/empty = proto3 default)
            Assert.That(fieldNumbers, Does.Not.Contain(5), "position (all zero)");
            Assert.That(fieldNumbers, Does.Not.Contain(8), "fps (0)");
            Assert.That(fieldNumbers, Does.Not.Contain(9), "frame_time_ms (0)");
            Assert.That(fieldNumbers, Does.Not.Contain(10), "memory_used_bytes (0)");
            Assert.That(fieldNumbers, Does.Not.Contain(11), "gpu_time_ms (0)");
            Assert.That(fieldNumbers, Does.Not.Contain(12), "attributes (null)");
            Assert.That(fieldNumbers, Does.Not.Contain(13), "metrics (null)");
            Assert.That(fieldNumbers, Does.Not.Contain(14), "source (Unspecified=0)");
            Assert.That(fieldNumbers, Does.Not.Contain(20), "game_thread_ms (0)");
            Assert.That(fieldNumbers, Does.Not.Contain(21), "render_thread_ms (0)");
        }

        [Test]
        public void Serialize_AttributesMap_EncodesAsRepeatedSubMessage()
        {
            var events = new[]
            {
                new TelemetryEvent
                {
                    EventName = "attr_test",
                    TimestampUs = 1L,
                    SessionId = "s",
                    Attributes = new List<StringPair>
                    {
                        new StringPair("key1", "val1"),
                        new StringPair("key2", "val2"),
                    },
                }
            };

            var bytes = TelemetrySerializer.Serialize(events);
            var batchFields = ParseFields(bytes);
            var (eventPayload, _) = ReadLengthDelimited(bytes, batchFields[0].valueOffset);
            var eventFields = ParseFields(eventPayload);

            // Count field 12 (attributes) entries
            int attrCount = 0;
            foreach (var f in eventFields)
            {
                if (f.field == 12)
                {
                    Assert.That(f.wireType, Is.EqualTo(2), "attributes entry is length-delimited");
                    attrCount++;
                }
            }
            Assert.That(attrCount, Is.EqualTo(2), "two attribute entries");
        }

        [Test]
        public void Serialize_MetricsMap_EncodesDoubleFromFloat()
        {
            var events = new[]
            {
                new TelemetryEvent
                {
                    EventName = "metric_test",
                    TimestampUs = 1L,
                    SessionId = "s",
                    Metrics = new List<FloatPair>
                    {
                        new FloatPair("score", 99.5f),
                    },
                }
            };

            var bytes = TelemetrySerializer.Serialize(events);
            var batchFields = ParseFields(bytes);
            var (eventPayload, _) = ReadLengthDelimited(bytes, batchFields[0].valueOffset);
            var eventFields = ParseFields(eventPayload);

            // Find field 13 (metrics) entry
            bool foundMetrics = false;
            foreach (var f in eventFields)
            {
                if (f.field == 13)
                {
                    Assert.That(f.wireType, Is.EqualTo(2), "metrics entry is length-delimited");
                    foundMetrics = true;

                    // Decode the map entry sub-message
                    var (entryPayload, _) = ReadLengthDelimited(eventPayload, f.valueOffset);
                    var entryFields = ParseFields(entryPayload);

                    // Entry should have field 1 (string key) and field 2 (double value)
                    var entryFieldNums = new HashSet<int>();
                    foreach (var ef in entryFields)
                        entryFieldNums.Add(ef.field);

                    Assert.That(entryFieldNums, Does.Contain(1), "key field");
                    Assert.That(entryFieldNums, Does.Contain(2), "value field (double)");

                    // Verify field 2 is wire type 1 (64-bit fixed = double)
                    foreach (var ef in entryFields)
                    {
                        if (ef.field == 2)
                            Assert.That(ef.wireType, Is.EqualTo(1), "double is wire type 1");
                    }
                }
            }
            Assert.That(foundMetrics, Is.True, "metrics field present");
        }

        [Test]
        public void Serialize_MultipleEvents_ProducesMultipleBatchEntries()
        {
            var events = new[]
            {
                new TelemetryEvent { EventName = "e1", TimestampUs = 1L, SessionId = "s" },
                new TelemetryEvent { EventName = "e2", TimestampUs = 2L, SessionId = "s" },
                new TelemetryEvent { EventName = "e3", TimestampUs = 3L, SessionId = "s" },
            };

            var bytes = TelemetrySerializer.Serialize(events);
            var batchFields = ParseFields(bytes);

            // All entries should be field 1 (repeated events)
            Assert.That(batchFields, Has.Count.EqualTo(3));
            foreach (var f in batchFields)
            {
                Assert.That(f.field, Is.EqualTo(1));
                Assert.That(f.wireType, Is.EqualTo(2));
            }
        }

        [Test]
        public void Serialize_EmptyArray_ProducesEmptyBytes()
        {
            var bytes = TelemetrySerializer.Serialize(Array.Empty<TelemetryEvent>());
            Assert.That(bytes, Is.Empty);
        }

        [Test]
        public void Serialize_EmptyListAttributes_OmitsField()
        {
            var events = new[]
            {
                new TelemetryEvent
                {
                    EventName = "empty_list",
                    TimestampUs = 1L,
                    SessionId = "s",
                    Attributes = new List<StringPair>(), // empty, not null
                    Metrics = new List<FloatPair>(),     // empty, not null
                }
            };

            var bytes = TelemetrySerializer.Serialize(events);
            var batchFields = ParseFields(bytes);
            var (eventPayload, _) = ReadLengthDelimited(bytes, batchFields[0].valueOffset);
            var eventFields = ParseFields(eventPayload);

            var fieldNumbers = new HashSet<int>();
            foreach (var f in eventFields)
                fieldNumbers.Add(f.field);

            // Empty lists should produce zero map entries (no field 12/13)
            Assert.That(fieldNumbers, Does.Not.Contain(12), "empty attributes list");
            Assert.That(fieldNumbers, Does.Not.Contain(13), "empty metrics list");
        }

        [Test]
        public void Serialize_PartialZeroPosition_StillEmitsSubMessage()
        {
            var events = new[]
            {
                new TelemetryEvent
                {
                    EventName = "pos_test",
                    TimestampUs = 1L,
                    SessionId = "s",
                    PositionX = 0f,
                    PositionY = 5.0f,
                    PositionZ = 0f,
                }
            };

            var bytes = TelemetrySerializer.Serialize(events);
            var batchFields = ParseFields(bytes);
            var (eventPayload, _) = ReadLengthDelimited(bytes, batchFields[0].valueOffset);
            var eventFields = ParseFields(eventPayload);

            var fieldNumbers = new HashSet<int>();
            foreach (var f in eventFields)
                fieldNumbers.Add(f.field);

            // Position sub-message should be present (Y is non-zero)
            Assert.That(fieldNumbers, Does.Contain(5), "position sub-message present");
        }
    }
}
