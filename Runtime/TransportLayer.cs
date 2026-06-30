using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Networking;

namespace Framedash
{
    /// <summary>
    /// Out-param holder for <see cref="TransportLayer.SendBatch"/>. Unity coroutines
    /// cannot return a value, so the caller passes one of these and reads it once the
    /// coroutine completes.
    /// </summary>
    public sealed class DeliveryResult
    {
        /// <summary>
        /// Number of events, counting from the front of the batch, that were delivered
        /// as one contiguous run. Delivery is "leading" because the offline-queue ack
        /// only cares about the persisted block at the head of the batch: events after
        /// the first non-delivered one are not counted even if a later split delivered
        /// them. 0 means nothing was delivered; events.Length means the whole batch was.
        /// </summary>
        public int DeliveredLeadingCount;
    }

    /// <summary>
    /// Handles HTTP transport of telemetry batches to the Framedash ingest endpoint.
    /// Uses Protobuf + gzip encoding. Delegates retry decisions to <see cref="RetryPolicy"/>.
    /// </summary>
    public sealed class TransportLayer
    {
        private readonly string _endpointUrl;
        private readonly string _apiKey;
        private readonly string _sdkVersion;
        private readonly int _maxPayloadBytes;
        private readonly RetryPolicy _retryPolicy;
        private readonly bool _disabled;

        public TransportLayer(string endpointUrl, string apiKey, string sdkVersion, int maxPayloadBytes)
        {
            // Fail closed: if the endpoint fails the transport-security check, DISABLE
            // sending rather than redirecting telemetry (and the configured API key) to
            // a host the developer never configured. Matches the UE5 SDK, which drops
            // batches on a failed check. Silently substituting the default ingest host
            // could ship a self-hosted or staging deployment's player data to the vendor
            // cloud (a data-residency/privacy problem), and the SDK already prefers
            // dropping telemetry over misbehaving (see EventBuffer). HTTP is allowed
            // only for a parsed loopback host; a substring check would accept
            // "http://localhost.attacker.com" and leak the key in cleartext.
            if (!EndpointSecurity.IsEndpointTransportSecure(endpointUrl))
            {
                Debug.LogError("[Framedash] Endpoint URL failed the transport-security check (must use HTTPS; HTTP is allowed only for localhost/127.0.0.1/[::1]). Telemetry is DISABLED until a secure endpoint is configured.");
                _disabled = true;
            }

            _endpointUrl = endpointUrl;
            _apiKey = apiKey;
            _sdkVersion = sdkVersion;
            _maxPayloadBytes = maxPayloadBytes;
            _retryPolicy = new RetryPolicy();
        }

        /// <summary>
        /// Serialize and send a batch of events using Protobuf + gzip. Reports how many
        /// leading events were delivered via <paramref name="result"/> so the caller can
        /// acknowledge persisted events and re-persist the undelivered tail.
        /// </summary>
        public IEnumerator SendBatch(TelemetryEvent[] events, DeliveryResult result)
        {
            // Never throw out of the SDK: tolerate a null out-param even though the only
            // in-SDK caller always passes one (writes below would otherwise NRE).
            result ??= new DeliveryResult();
            // Reset before any early return so the result always reflects THIS send, even
            // if a caller reuses the instance across batches.
            result.DeliveredLeadingCount = 0;
            if (events == null || events.Length == 0) yield break;

            // Fail closed: an endpoint that did not pass the security check disables the
            // transport entirely (matches the UE5 SDK dropping batches). Report the batch
            // as handled so the offline queue drains rather than accumulating forever
            // against a misconfigured endpoint; the error was already logged once at
            // construction, so stay quiet here to avoid spam.
            if (_disabled)
            {
                result.DeliveredLeadingCount = events.Length;
                yield break;
            }

            // Chunk to the SERVER per-request caps (event count AND decoded-entry
            // count = events + all attribute/metric map entries), NOT the per-flush
            // batch threshold. The consumer rejects an over-cap batch wholesale, so a
            // drain larger than a cap (the buffer can hold up to 2x the flush batch
            // size) is split here, before serialization. A normal sub-cap drain is
            // sent as one request and chunked only by the payload-byte limit below,
            // so a stall/burst drain is not fragmented into many tiny requests.
            if (BatchPolicy.ExceedsWireCaps(events))
            {
                yield return SplitAndResend(events, result);
                yield break;
            }

            byte[] payload;
            try
            {
                payload = Compress(TelemetrySerializer.Serialize(events));
            }
            catch (Exception e)
            {
                // A serialization failure is deterministic, so persisting the batch would
                // only reload a poison payload that fails again every run. Report it as
                // handled to drop it instead of wedging the offline queue.
                Debug.LogError($"[Framedash] Serialization failed: {e.Message}");
                result.DeliveredLeadingCount = events.Length;
                yield break;
            }

            // If payload exceeds max, split batch in half and retry
            if (payload.Length > _maxPayloadBytes && events.Length > 1)
            {
                yield return SplitAndResend(events, result);
                yield break;
            }

            for (int attempt = 0; attempt < _retryPolicy.MaxRetries; attempt++)
            {
                using (var request = new UnityWebRequest(_endpointUrl, "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(payload);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = 30;
                    request.redirectLimit = 0;
                    request.SetRequestHeader("Content-Type", "application/x-protobuf");
                    request.SetRequestHeader("Content-Encoding", "gzip");
                    request.SetRequestHeader("X-API-Key", _apiKey);
                    request.SetRequestHeader("X-SDK-Version", _sdkVersion);

                    yield return request.SendWebRequest();

                    var action = _retryPolicy.Classify(
                        request.responseCode, attempt, events.Length);

                    switch (action)
                    {
                        case RetryAction.Success:
                            result.DeliveredLeadingCount = events.Length;
                            yield break;

                        case RetryAction.SplitBatch:
                            yield return SplitAndResend(events, result);
                            yield break;

                        case RetryAction.Fail:
                            // A non-retryable failure inside the retry loop is a permanent
                            // client error (4xx other than 429, a surfaced 3xx, or a single
                            // event too large to split) -- it can never succeed. Report it
                            // as handled so the batch is dropped, not persisted: persisting
                            // a poison payload would refill the capped queue every launch
                            // and block newer telemetry behind events that always fail.
                            // (Retry exhaustion on a transient code falls through below with
                            // DeliveredLeadingCount = 0, so those events ARE persisted.)
                            Debug.LogWarning($"[Framedash] Send failed permanently (HTTP {request.responseCode}); dropping {events.Length} event(s): {request.downloadHandler.text}");
                            result.DeliveredLeadingCount = events.Length;
                            yield break;

                        case RetryAction.Retry:
                            float delay = _retryPolicy.GetRetryDelaySeconds(attempt);
                            Debug.LogWarning($"[Framedash] Retry {attempt + 1}/{_retryPolicy.MaxRetries} in {delay:F1}s (HTTP {request.responseCode})");
                            // Real-time wait so an app pause / Time.timeScale == 0 does not
                            // stall retry backoff (WaitForSeconds is scaled by Time.timeScale).
                            yield return new WaitForSecondsRealtime(delay);
                            break;
                    }
                }
            }

            // Retries exhausted: nothing delivered (DeliveredLeadingCount stays 0) so the
            // caller persists the batch instead of dropping it.
            Debug.LogWarning($"[Framedash] Failed to send batch after {_retryPolicy.MaxRetries} retries. Persisting {events.Length} event(s) for a later run.");
        }

        private IEnumerator SplitAndResend(TelemetryEvent[] events, DeliveryResult result)
        {
            int mid = events.Length / 2;
            var firstHalf = new TelemetryEvent[mid];
            var secondHalf = new TelemetryEvent[events.Length - mid];
            Array.Copy(events, 0, firstHalf, 0, mid);
            Array.Copy(events, mid, secondHalf, 0, events.Length - mid);

            var firstResult = new DeliveryResult();
            yield return SendBatch(firstHalf, firstResult);
            var secondResult = new DeliveryResult();
            yield return SendBatch(secondHalf, secondResult);

            // "Leading delivered" is contiguous from the front, so the second half only
            // extends it when the first half was delivered in full. If the first half is
            // partial, the leading count stops there and the caller persists everything
            // from that boundary on -- any events the second half did deliver may be
            // re-sent next run, a rare duplicate we accept so an event is never lost.
            result.DeliveredLeadingCount = firstResult.DeliveredLeadingCount == firstHalf.Length
                ? firstHalf.Length + secondResult.DeliveredLeadingCount
                : firstResult.DeliveredLeadingCount;
        }

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
