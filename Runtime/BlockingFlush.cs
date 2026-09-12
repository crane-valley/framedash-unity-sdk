#nullable enable

using System;
using System.IO;
using System.IO.Compression;

namespace Framedash
{
    /// <summary>
    /// Pure, engine-independent budget + state-machine logic for the synchronous
    /// <c>FlushBlocking</c> path (see <see cref="TelemetrySDK.FlushBlocking"/>). Kept
    /// free of UnityEngine and socket types so the timeout clamp, the per-step budget
    /// checks, and the leading-delivered-count reconciliation are NextUnit-testable
    /// (TelemetrySDK and the blocking socket sender are excluded from the test
    /// assembly). The actual network POST is injected as a delegate so the chunking /
    /// budget / leading-count decisions can be exercised with a fake transport.
    /// </summary>
    public static class BlockingFlush
    {
        /// <summary>
        /// Hard ceiling on the blocking-flush budget. A caller cannot block the main
        /// thread longer than this even by asking, matching the ~30s wall-time the
        /// async retry ladder is bounded to.
        /// </summary>
        public const int MaxTimeoutMs = 30000;

        /// <summary>
        /// POST one already-serialized+gzipped payload synchronously. Returns the HTTP
        /// status code, or 0 for any transport-level failure / budget exhaustion
        /// (mirrors <c>UnityWebRequest.responseCode</c>), so the caller classifies it
        /// exactly like the async path. Must never throw (fail-safe contract).
        /// </summary>
        public delegate long BlockingPost(byte[] payload);

        /// <summary>
        /// Returns a non-positive value when the caller must fast-fail: a timeout of zero or
        /// less buys no delivery, so the whole call is refused (the caller returns false).
        /// Otherwise the budget is clamped to `MaxTimeoutMs`. Keeping the `&lt;= 0` gate here
        /// (not only in TelemetrySDK) makes the budget contract NextUnit-testable.
        /// </summary>
        public static int ResolveBudgetMs(int timeoutMs)
        {
            if (timeoutMs <= 0) return 0;
            return timeoutMs > MaxTimeoutMs ? MaxTimeoutMs : timeoutMs;
        }

        /// <summary>
        /// Per-candidate CONNECT-phase budget for the IPv4-then-IPv6 fallback, so a
        /// blackholing preferred address cannot consume the whole budget and starve a
        /// reachable later candidate (e.g. IPv6-only / broken-IPv4 networks). Candidate
        /// <paramref name="index"/> (0-based) of <paramref name="candidateCount"/> gets
        /// <c>remainingMs / (candidateCount - index)</c>: the LAST (or only) candidate gets
        /// the full remaining budget, earlier ones reserve an even share for those after
        /// them. This caps only the connect+handshake; the successfully-connected candidate's
        /// request/response phase may still use the full remaining budget. Deterministic and
        /// never negative; a degenerate index &gt;= count yields the full remaining.
        /// </summary>
        public static long ConnectBudgetMs(long remainingMs, int candidateCount, int index)
        {
            if (remainingMs <= 0) return 0;
            int remainingCandidates = candidateCount - index;
            // Last / only / degenerate (index >= count): no later candidate to reserve for.
            if (remainingCandidates <= 1) return remainingMs;
            return remainingMs / remainingCandidates;
        }

        /// <summary>
        /// Drive the chunked, budget-bounded blocking send of one contiguous batch and
        /// return the number of leading events CONFIRMED delivered (a contiguous run
        /// from the front), so the caller can ack exactly the delivered persisted prefix
        /// (DropOldest) and keep the undelivered tail. Only an HTTP 2xx counts as
        /// delivered; any other status (permanent 4xx, 5xx, or a transport-level 0)
        /// leaves the leading count at the failure boundary so nothing is lost.
        ///
        /// The budget is shared across every split child (over-cap or over-payload
        /// batches are halved), so the total wall time stays within one budget
        /// regardless of split depth. <paramref name="elapsedMs"/> is injected (rather
        /// than a captured Stopwatch) so a test can drive the clock deterministically.
        /// </summary>
        public static int SendLeading(
            TelemetryEvent[]? events, int maxPayloadBytes,
            Func<long> elapsedMs, long budgetMs, BlockingPost post)
        {
            return SendLeading(events, maxPayloadBytes, elapsedMs, budgetMs, post, BuildPayload, true);
        }

        /// <summary>
        /// Overload exposing <paramref name="allowChunkBytesSplit"/>: pass false for the
        /// reclaimed in-flight envelope so its array shape is preserved (see the internal
        /// overload). Production entry point for the two envelope kinds.
        /// <paramref name="withinBudget"/> reports the STRICT budget contract: false when
        /// any confirmation (even a successful 2xx) arrived at or after the deadline. The
        /// late-confirmed events are still counted in the returned delivered count -- the
        /// server accepted them, so the caller must ACK them (never resend, no duplicate) --
        /// but the overall FlushBlocking result must then be false, reporting only that the
        /// confirmation missed the budget.
        /// </summary>
        public static int SendLeading(
            TelemetryEvent[]? events, int maxPayloadBytes,
            Func<long> elapsedMs, long budgetMs, BlockingPost post, bool allowChunkBytesSplit,
            out bool withinBudget)
        {
            bool overran = false;
            int delivered = SendLeading(
                events, maxPayloadBytes, elapsedMs, budgetMs, post, BuildPayload, allowChunkBytesSplit, ref overran);
            withinBudget = !overran;
            return delivered;
        }

        /// <summary>
        /// Overload with an injectable payload builder (serialize + gzip). Production uses
        /// the real <see cref="BuildPayload"/>; tests inject a builder that can throw,
        /// because <see cref="TelemetrySerializer"/> is robustly null-safe and does not
        /// throw on any constructible <see cref="TelemetryEvent"/>, which would otherwise
        /// leave the serialize-failure branch unreachable from a test.
        ///
        /// <paramref name="allowChunkBytesSplit"/> enables the pre-serialize size split
        /// (see below); it is true for the freshly-buffered envelope and false for the
        /// reclaimed in-flight envelope, whose identical array shape must be preserved so
        /// the consumer's per-POST dedup token still matches the parked async send.
        /// </summary>
        internal static int SendLeading(
            TelemetryEvent[]? events, int maxPayloadBytes,
            Func<long> elapsedMs, long budgetMs, BlockingPost post,
            Func<TelemetryEvent[], byte[]> buildPayload, bool allowChunkBytesSplit = true)
        {
            bool overranIgnored = false;
            return SendLeading(
                events, maxPayloadBytes, elapsedMs, budgetMs, post, buildPayload, allowChunkBytesSplit,
                ref overranIgnored);
        }

        private static int SendLeading(
            TelemetryEvent[]? events, int maxPayloadBytes,
            Func<long> elapsedMs, long budgetMs, BlockingPost post,
            Func<TelemetryEvent[], byte[]> buildPayload, bool allowChunkBytesSplit,
            ref bool budgetOverran)
        {
            if (events == null || events.Length == 0) return 0;

            // Over the server per-request caps (event count OR decoded entries): the
            // consumer rejects an over-cap batch wholesale, so split before serializing.
            // Applied to BOTH envelope kinds -- the async path splits on the same caps, so
            // an in-flight resend keeps the same per-POST array shape.
            if (BatchPolicy.ExceedsWireCaps(events))
                return SplitLeading(events, maxPayloadBytes, elapsedMs, budgetMs, post, buildPayload, allowChunkBytesSplit, ref budgetOverran);

            if (elapsedMs() >= budgetMs) return 0;

            // Bound the CPU of the (uninterruptible) serialize+gzip BEFORE running it: a
            // legal attribute-heavy chunk can be tens of MB and would blow the budget in one
            // uninterruptible shot with no budget check in between.
            if (BatchPolicy.ExceedsBlockingChunkBytes(events))
            {
                // Buffered envelope (no parked async owner): split by ESTIMATED size so each
                // serialize stays within MaxBlockingChunkBytes and the budget is re-checked
                // between chunks; one bounded chunk's serialize is the accepted residual
                // uninterruptible unit.
                if (allowChunkBytesSplit)
                    return SplitLeading(events, maxPayloadBytes, elapsedMs, budgetMs, post, buildPayload, allowChunkBytesSplit, ref budgetOverran);

                // Reclaimed in-flight envelope: it must keep its identical array shape so the
                // consumer's per-POST dedup token still matches the parked async send, so it
                // cannot be split here. Rather than serialize the WHOLE oversized batch in one
                // uninterruptible shot -- which a tiny timeout could not bound -- DEFER it:
                // report it wholly undelivered (0) WITHOUT serializing, so FlushBlocking
                // returns false and the existing retention machinery owns delivery (persist
                // with the offline queue on / re-buffer with it off, re-sending the SAME array
                // later, dedup-safe). Conservative and rare: a normal in-flight batch is far
                // below MaxBlockingChunkBytes; only a restored ~1000-event attribute-heavy
                // prefix reaches this.
                return 0;
            }

            byte[] payload;
            try
            {
                payload = buildPayload(events);
            }
            catch
            {
                // A serialize/gzip failure sent no HTTP request, so report NOTHING
                // delivered: the batch is retained via the caller's no-loss path and
                // FlushBlocking returns false rather than a false success. This
                // deliberately differs from the async SendBatch, which drops a serialize
                // failure to avoid reloading a poison payload every run -- that
                // deterministic-poison drop policy is OWNED by the periodic flush path,
                // which retries and drops the same batch, so retaining here cannot wedge
                // the queue permanently.
                return 0;
            }

            // Re-check the budget AFTER the (now-bounded) serialize+gzip before adding
            // network time on top.
            if (elapsedMs() >= budgetMs) return 0;

            if (payload.Length > maxPayloadBytes && events.Length > 1)
                return SplitLeading(events, maxPayloadBytes, elapsedMs, budgetMs, post, buildPayload, allowChunkBytesSplit, ref budgetOverran);

            long status = post(payload);
            if (status >= 200 && status < 300)
            {
                // STRICT budget contract: a 2xx that arrives at/after the deadline is a real
                // delivery -- the events MUST still be counted so the caller acks them
                // (resending would duplicate) -- but the confirmation missed the budget, so
                // flag the overrun and the overall FlushBlocking result becomes false.
                if (elapsedMs() >= budgetMs) budgetOverran = true;
                return events.Length;
            }
            // A 413 on a multi-event batch is splittable (each half may fit); everything
            // else is not delivered, and the leading run stops here.
            if (status == 413 && events.Length > 1)
                return SplitLeading(events, maxPayloadBytes, elapsedMs, budgetMs, post, buildPayload, allowChunkBytesSplit, ref budgetOverran);
            return 0;
        }

        // Split a batch in half and send both children under the SAME shared budget.
        // "Leading delivered" is contiguous from the front, so the second half only
        // extends the count when the first half was delivered in full (mirrors the
        // async SplitAndResend leading-count rule). allowChunkBytesSplit is threaded
        // through so the in-flight envelope's identical-shape rule holds at every depth.
        private static int SplitLeading(
            TelemetryEvent[] events, int maxPayloadBytes,
            Func<long> elapsedMs, long budgetMs, BlockingPost post,
            Func<TelemetryEvent[], byte[]> buildPayload, bool allowChunkBytesSplit,
            ref bool budgetOverran)
        {
            int mid = events.Length / 2;
            var first = new TelemetryEvent[mid];
            var second = new TelemetryEvent[events.Length - mid];
            Array.Copy(events, 0, first, 0, mid);
            Array.Copy(events, mid, second, 0, second.Length);

            int firstLeading = SendLeading(first, maxPayloadBytes, elapsedMs, budgetMs, post, buildPayload, allowChunkBytesSplit, ref budgetOverran);
            if (firstLeading != first.Length) return firstLeading;
            int secondLeading = SendLeading(second, maxPayloadBytes, elapsedMs, budgetMs, post, buildPayload, allowChunkBytesSplit, ref budgetOverran);
            return first.Length + secondLeading;
        }

        private static byte[] BuildPayload(TelemetryEvent[] events)
            => Compress(TelemetrySerializer.Serialize(events));

        private static byte[] Compress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress))
                {
                    gzip.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }
    }
}
