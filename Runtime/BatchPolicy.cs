#nullable enable

namespace Framedash
{
    /// <summary>
    /// Pure batch-sizing policy for the telemetry transport. No Unity
    /// dependencies, so it is unit-testable under NextUnit (TransportLayer itself is
    /// engine-coupled via UnityWebRequest and excluded from the test assembly).
    ///
    /// The split decision keys off the SERVER per-request caps, not the per-flush
    /// batch threshold: a normal sub-cap drain is sent as a single request (then
    /// bounded only by the payload-byte limit), so a stall/burst drain is not
    /// fragmented into many tiny requests.
    /// </summary>
    public static class BatchPolicy
    {
        public const int MaxEventsPerBatch = 10000;

        /// <summary>
        /// Server-side per-request decoded-object cap (mirrors
        /// packages/ingest-core/src/config.ts MAX_DECODED_ENTRIES): events PLUS
        /// every attributes/metrics map entry across all events. The consumer
        /// rejects a batch whose total exceeds this wholesale, even when the event
        /// count, the per-event attribute/metric counts, and the gzip payload size
        /// are each within their own limits -- e.g. 10,000 events with 10 attributes
        /// each is 110,000 entries. The SDK must chunk on this too, otherwise such a
        /// batch is sent whole and dropped by the server.
        /// </summary>
        public const int MaxDecodedEntries = 100000;

        public static int CountDecodedEntries(TelemetryEvent[]? events)
        {
            if (events == null) return 0;
            // TelemetryEvent is a struct (value type), so every array element is a
            // fully-initialized value -- there are no null elements to guard, and
            // events[i].Attributes/Metrics cannot throw NullReferenceException (an
            // unset map is simply a null List, handled below). Start the total at one
            // entry per event, then add each event's map entries.
            int total = events.Length;
            for (int i = 0; i < events.Length; i++)
            {
                var attributes = events[i].Attributes;
                var metrics = events[i].Metrics;
                if (attributes != null) total += attributes.Count;
                if (metrics != null) total += metrics.Count;
            }
            return total;
        }

        /// <summary>
        /// Whether the batch must be chunked before sending because it would be
        /// rejected wholesale by a server per-request cap -- either the event-count
        /// wire cap or the decoded-entry cap (events + all map entries). A batch of
        /// one (or zero) is never split here: a single oversized event is bounded by
        /// the payload-byte path or dropped on a 413, and the server enforces the
        /// per-event attribute/metric caps that splitting cannot fix.
        /// </summary>
        public static bool ExceedsWireCaps(TelemetryEvent[]? events)
        {
            if (events == null || events.Length <= 1) return false;
            return events.Length > MaxEventsPerBatch
                || CountDecodedEntries(events) > MaxDecodedEntries;
        }

        // Rough per-event byte estimate for the FIXED-SIZE parts only (numeric fields,
        // position, timestamp, protobuf framing) of the blocking serialize-size split.
        // Per-event STRING fields (event name, ids, platform, engine version) are counted
        // by their actual lengths below -- legal values (128-char ids etc.) can add ~2KB
        // UTF-8 per event, far beyond any flat base. Deliberately generous so the estimate
        // is not an undercount that lets an oversized chunk through. Mirrors the Godot SDK.
        private const int EventBaseByteEstimate = 256;
        private const int MapEntryOverheadBytes = 8;
        private const int MetricEntryByteEstimate = 16;
        // Multiplier converting UTF-16 char counts (string.Length) to a conservative UTF-8
        // byte estimate: protobuf serializes strings as UTF-8, where a BMP char is up to 3
        // bytes. A supplementary char is 2 UTF-16 units encoding to 4 UTF-8 bytes (2 bytes
        // per unit), so x3 covers it too. Without this a CJK/non-ASCII attribute value could
        // be ~3x the estimate, letting a multi-MiB chunk slip under the blocking cap and
        // defeat the timeout bound. Mirrored across the Godot/UE5 SDKs.
        private const int Utf8BytesPerCharEstimate = 3;

        /// <summary>
        /// Max estimated UNCOMPRESSED bytes a single blocking serialize+gzip may process
        /// before the batch is split. serialize+gzip is uninterruptible, so this bounds one
        /// CPU unit: ~1 MiB serializes and gzips in a few milliseconds even on weak hardware,
        /// keeping the synchronous FlushBlocking within its time budget. Mirrors the Godot SDK.
        /// </summary>
        public const long MaxBlockingChunkBytes = 1 << 20;

        /// <summary>
        /// Conservative estimate of a batch's uncompressed serialized size in bytes, used
        /// ONLY to bound the work of one serialize+gzip on the synchronous blocking flush.
        /// An attribute-heavy legal batch (up to <see cref="MaxDecodedEntries"/> map entries
        /// whose values can each be hundreds of chars) can otherwise be tens of MB, and a
        /// single uninterruptible serialize could overrun the budget. String lengths are
        /// UTF-16 char counts, which track serialize/gzip CPU cost closely enough for a
        /// chunking threshold. Mirrors the Godot SDK.
        /// </summary>
        public static long EstimateSerializedBytes(TelemetryEvent[]? events)
        {
            if (events == null) return 0;
            long total = 0;
            for (int i = 0; i < events.Length; i++)
            {
                total += EventBaseByteEstimate;
                // ALL per-event string fields of TelemetryEvent at their actual lengths
                // (x3 UTF-8): omitting them would let 128-char ids / CJK names add multiple
                // unestimated KB per event and defeat the chunk bound.
                total += (StringLength(events[i].EventName)
                    + StringLength(events[i].SessionId)
                    + StringLength(events[i].PlayerId)
                    + StringLength(events[i].MapId)
                    + StringLength(events[i].BuildId)
                    + StringLength(events[i].Platform)
                    + StringLength(events[i].EngineVersion)) * (long)Utf8BytesPerCharEstimate;
                var attributes = events[i].Attributes;
                var metrics = events[i].Metrics;
                if (attributes != null)
                {
                    for (int a = 0; a < attributes.Count; a++)
                    {
                        string key = attributes[a].Key;
                        string value = attributes[a].Value;
                        total += ((key != null ? key.Length : 0)
                            + (value != null ? value.Length : 0)) * Utf8BytesPerCharEstimate
                            + MapEntryOverheadBytes;
                    }
                }
                if (metrics != null)
                {
                    for (int m = 0; m < metrics.Count; m++)
                    {
                        string key = metrics[m].Key;
                        total += (key != null ? key.Length : 0) * Utf8BytesPerCharEstimate + MetricEntryByteEstimate;
                    }
                }
            }
            return total;
        }

        private static int StringLength(string value) => value != null ? value.Length : 0;

        /// <summary>
        /// A single event is never split here: it cannot be broken down further, and one
        /// bounded chunk's serialize is the accepted residual uninterruptible unit. Mirrors the
        /// Godot SDK.
        /// </summary>
        public static bool ExceedsBlockingChunkBytes(TelemetryEvent[]? events)
        {
            if (events == null || events.Length <= 1) return false;
            return EstimateSerializedBytes(events) > MaxBlockingChunkBytes;
        }

        /// <summary>
        /// Ordered list of INDEPENDENT envelopes to POST on the synchronous FlushBlocking
        /// path. The freshly-buffered events and a reclaimed in-flight batch are kept as
        /// SEPARATE envelopes, never concatenated: the consumer deduplicates by hashing the
        /// full ordered event array (apps/consumer/src/message-helpers.ts hashEventBatch),
        /// so re-sending the in-flight batch with its ORIGINAL array keeps the same dedup
        /// token and is dropped if it already reached ingest, whereas an "in-flight +
        /// buffered" concatenation would hash differently and duplicate every in-flight
        /// event (both charged and inserted). <paramref name="buffered"/> is ordered FIRST
        /// because it is guaranteed-undelivered, so a wedged endpoint under the shared budget
        /// cannot starve it behind a possibly-redundant in-flight resend. Empty/null batches
        /// are omitted. Mirrors the Godot SDK's BuildShutdownEnvelopes.
        /// </summary>
        public static TelemetryEvent[][] BuildBlockingEnvelopes(TelemetryEvent[]? buffered, TelemetryEvent[]? inFlight)
        {
            bool haveBuffered = buffered != null && buffered.Length > 0;
            bool haveInFlight = inFlight != null && inFlight.Length > 0;
            if (haveBuffered && haveInFlight) return new[] { buffered!, inFlight! };
            if (haveBuffered) return new[] { buffered! };
            if (haveInFlight) return new[] { inFlight! };
            return System.Array.Empty<TelemetryEvent[]>();
        }
    }
}
