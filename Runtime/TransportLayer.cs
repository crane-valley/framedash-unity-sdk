using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Networking;

namespace Framedash
{
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
        /// Serialize and send a batch of events using Protobuf + gzip.
        /// </summary>
        public IEnumerator SendBatch(TelemetryEvent[] events)
        {
            // Fail closed: an endpoint that did not pass the security check disables
            // the transport entirely (matches the UE5 SDK dropping batches). The error
            // was already logged once at construction; stay quiet here to avoid spam.
            if (_disabled || events == null || events.Length == 0) yield break;

            // Chunk to the SERVER per-request caps (event count AND decoded-entry
            // count = events + all attribute/metric map entries), NOT the per-flush
            // batch threshold. The consumer rejects an over-cap batch wholesale, so a
            // drain larger than a cap (the buffer can hold up to 2x the flush batch
            // size) is split here, before serialization. A normal sub-cap drain is
            // sent as one request and chunked only by the payload-byte limit below,
            // so a stall/burst drain is not fragmented into many tiny requests.
            if (BatchPolicy.ExceedsWireCaps(events))
            {
                yield return SplitAndResend(events);
                yield break;
            }

            byte[] payload;
            try
            {
                payload = Compress(TelemetrySerializer.Serialize(events));
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] Serialization failed: {e.Message}");
                yield break;
            }

            // If payload exceeds max, split batch in half and retry
            if (payload.Length > _maxPayloadBytes && events.Length > 1)
            {
                yield return SplitAndResend(events);
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
                            yield break;

                        case RetryAction.SplitBatch:
                            yield return SplitAndResend(events);
                            yield break;

                        case RetryAction.Fail:
                            Debug.LogWarning($"[Framedash] Send failed (HTTP {request.responseCode}): {request.downloadHandler.text}");
                            yield break;

                        case RetryAction.Retry:
                            float delay = _retryPolicy.GetRetryDelaySeconds(attempt);
                            Debug.LogWarning($"[Framedash] Retry {attempt + 1}/{_retryPolicy.MaxRetries} in {delay:F1}s (HTTP {request.responseCode})");
                            yield return new WaitForSeconds(delay);
                            break;
                    }
                }
            }

            Debug.LogError($"[Framedash] Failed to send batch after {_retryPolicy.MaxRetries} retries. Dropping {events.Length} events.");
        }

        private IEnumerator SplitAndResend(TelemetryEvent[] events)
        {
            int mid = events.Length / 2;
            var firstHalf = new TelemetryEvent[mid];
            var secondHalf = new TelemetryEvent[events.Length - mid];
            Array.Copy(events, 0, firstHalf, 0, mid);
            Array.Copy(events, mid, secondHalf, 0, events.Length - mid);

            yield return SendBatch(firstHalf);
            yield return SendBatch(secondHalf);
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
