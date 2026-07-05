using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
#if !UNITY_WEBGL
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
#endif
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

        /// <summary>
        /// Whole-request timeout in seconds, applied identically to the primary
        /// UnityWebRequest attempt and to the direct-socket fallback attempt
        /// (connect + TLS handshake + request + status read).
        /// </summary>
        private const int RequestTimeoutSeconds = 10;

#if !UNITY_WEBGL
        // DNS resolution cap for building the fallback delivery plan: long enough for
        // a cold lookup, short enough not to dominate a flush. Mirrors the Godot SDK's
        // ~3s resolve cap.
        private const float ResolveTimeoutSeconds = 3f;

        /// <summary>
        /// Cached prefer-IPv4-with-IPv6-fallback delivery plan (resolved IP-literal
        /// URLs + Host header + TLS common-name, see EndpointAddressPlanner). Resolved
        /// lazily on the FIRST transport-level failure -- a healthy client never pays
        /// the extra DNS queries -- and reused afterwards: the ingest endpoint is fixed
        /// and Cloudflare anycast DNS is stable. Null until then.
        /// </summary>
        private EndpointAddressPlan _plan;

        /// <summary>
        /// True once <see cref="_plan"/> is permanent and needs no rebuild: a
        /// successful resolved plan, OR a STRUCTURAL passthrough (loopback /
        /// IP-literal / non-HTTPS endpoint -- deterministic for a fixed endpoint). A
        /// RESOLUTION-FAILED passthrough (the endpoint qualifies but neither family
        /// resolved in time) leaves this false so a later flush retries resolution and
        /// a transient startup DNS failure does not permanently disable the fallback.
        /// </summary>
        private bool _planCacheFinal;

        /// <summary>
        /// The single in-flight DNS resolve task, shared across SendBatch calls. A
        /// resolve that outlives one flush's poll cap is RE-POLLED by the next flush
        /// instead of spawning a new task, so a wedged resolver holds exactly one
        /// thread-pool worker instead of accumulating one per flush. Cleared when the
        /// task completes and its result has been consumed (success or failure), so a
        /// FAILED resolution triggers a fresh DNS attempt on a later flush.
        /// </summary>
        private Task<ValueTuple<string, string>> _resolveTask;

        // Out-param holder for the fallback coroutine (coroutines cannot return).
        private sealed class FallbackResult
        {
            public long StatusCode;
        }
#endif

        /// <summary>
        /// Opt-in verbose transport logging (F29). When true, each send attempt logs
        /// the endpoint + compressed payload size and each successful batch logs
        /// "Flushed N events (HTTP 202)" via <see cref="TransportLog"/>. Default false
        /// so a shipping game emits nothing on the happy path; first-time integrators
        /// flip it on to confirm delivery client-side. Settable so a runtime toggle on
        /// the owning <see cref="TelemetrySDK"/> takes effect on the live transport.
        /// </summary>
        public bool VerboseLogging { get; set; }

        public TransportLayer(string endpointUrl, string apiKey, string sdkVersion, int maxPayloadBytes, bool verboseLogging = false)
        {
            VerboseLogging = verboseLogging;
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

            // Opt-in verbose send confirmation (F29): report the endpoint + compressed
            // payload size once per contiguous batch, before the first attempt. Guarded
            // so an off (default) transport builds no string and logs nothing. Split
            // resends recurse into SendBatch and log their own smaller sub-batches.
            if (VerboseLogging)
            {
                Debug.Log(TransportLog.FormatSendAttempt(events.Length, payload.Length, _endpointUrl));
            }

#if !UNITY_WEBGL
            // familyIndex walks _plan.AttemptUrls (IPv4 -> IPv6) for the direct-socket
            // fallback. It advances only when the FALLBACK itself fails at the
            // transport level (status 0), never on a real HTTP response (a 5xx/429
            // means the server was reached, so switching family is pointless).
            // planResolveAttempted bounds the resolve wait to once per SendBatch so a
            // persistent DNS failure cannot stack multi-second waits across attempts.
            //
            // ATTEMPT-ACCOUNTING CONTRACT (deliberate change from the pre-fallback
            // transport): MaxRetries bounds PRIMARY (UnityWebRequest) attempts, and
            // each transport-level primary failure may add ONE direct-socket fallback
            // POST within the same attempt. Worst case (total blackout, both paths
            // timing out every attempt) is therefore 2 x MaxRetries POSTs and roughly
            // 2 x RequestTimeoutSeconds (~20s) wall time per attempt, in exchange for
            // in-flush delivery whenever either path works. The loop shape, the
            // MaxRetries denominator in retry logs, and the final-attempt backoff
            // skip are unchanged.
            int familyIndex = 0;
            bool planResolveAttempted = false;
#endif

            for (int attempt = 0; attempt < _retryPolicy.MaxRetries; attempt++)
            {
                using (var request = new UnityWebRequest(_endpointUrl, "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(payload);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    // Whole-request timeout. Bounded to 10s (not 30s) so a broken-IPv6
                    // network -- a global AAAA advertised via Router Advertisement with no
                    // working route, where UnityWebRequest has no Happy Eyeballs and an
                    // OS-resolver AAAA-first pick wedges the connect -- fails fast instead
                    // of stalling the flush for 30s. Fail-fast is paired with an ACTIVE
                    // address-family fallback below: a transport-level failure
                    // (responseCode 0) triggers a direct-socket TLS retry pinned to a
                    // resolved IPv4 (then IPv6) literal within the SAME attempt, so a
                    // broken-IPv6 client delivers in-flush instead of relying on the
                    // offline queue and the next run. The default-on offline queue
                    // remains the safety net when both paths fail, and the ONLY net on
                    // WebGL, where sockets do not exist and the fallback is compiled out.
                    request.timeout = RequestTimeoutSeconds;
                    request.redirectLimit = 0;
                    request.SetRequestHeader("Content-Type", "application/x-protobuf");
                    request.SetRequestHeader("Content-Encoding", "gzip");
                    request.SetRequestHeader("X-API-Key", _apiKey);
                    request.SetRequestHeader("X-SDK-Version", _sdkVersion);

                    yield return request.SendWebRequest();

                    long responseCode = request.responseCode;
                    bool usedFallback = false;

#if !UNITY_WEBGL
                    // Prefer-IPv4-with-IPv6-fallback (parity with the Godot SDK): a
                    // transport-level failure (responseCode 0: DNS/TLS/timeout/reset)
                    // on the primary UnityWebRequest attempt triggers a direct-socket
                    // TLS retry pinned to a resolved IP literal, IPv4 first. The
                    // normal path stays UnityWebRequest; only the failure path pays
                    // for the fallback. UnityWebRequest itself cannot pin a family:
                    // it forbids overriding the Host header and its CertificateHandler
                    // cannot safely validate an IP-literal connect (see
                    // DirectSocketSender), hence the raw TcpClient + SslStream path.
                    if (responseCode == 0)
                    {
                        if (!planResolveAttempted)
                        {
                            planResolveAttempted = true;
                            yield return EnsureDeliveryPlan();
                        }
                        if (_plan != null && !_plan.IsPassthrough)
                        {
                            if (VerboseLogging)
                            {
                                Debug.Log($"[Framedash] Transport-level failure on primary connect; direct-socket fallback to {_plan.AttemptUrls[familyIndex]}");
                            }
                            var fallback = new FallbackResult();
                            yield return SendViaDirectSocket(payload, familyIndex, fallback);
                            responseCode = fallback.StatusCode;
                            usedFallback = true;
                            // Fallback also failed at transport level: TOGGLE to the
                            // other family for the next attempt (IPv4 <-> IPv6,
                            // wrapping), so an IPv6-only network delivers over IPv6
                            // AND a broken-IPv6 network returns to the working IPv4
                            // after a transient glitch instead of wedging on the IPv6
                            // blackhole. A real HTTP status keeps the same family.
                            if (responseCode == 0)
                            {
                                familyIndex = EndpointAddressPlanner.NextFamily(familyIndex, _plan.AttemptUrls.Count);
                            }
                        }
                    }
#endif

                    var action = _retryPolicy.Classify(
                        responseCode, attempt, events.Length);

                    switch (action)
                    {
                        case RetryAction.Success:
                            // Opt-in positive delivery confirmation (F29): off by default
                            // so it never spams a shipping game; first-time integrators flip
                            // VerboseLogging to confirm delivery client-side.
                            if (VerboseLogging)
                            {
                                Debug.Log(TransportLog.FormatFlushSuccess(events.Length, responseCode));
                            }
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
                            // The direct-socket fallback only parses the status line
                            // (all the classification needs), so it has no body text.
                            Debug.LogWarning($"[Framedash] Send failed permanently (HTTP {responseCode}); dropping {events.Length} event(s): {(usedFallback ? "(direct-socket fallback; no response body captured)" : request.downloadHandler.text)}");
                            result.DeliveredLeadingCount = events.Length;
                            yield break;

                        case RetryAction.Retry:
                            // Only back off when another attempt will follow. The final
                            // attempt's backoff is pure waste -- the loop is about to exit and
                            // the batch is persisted for the next run -- and on a broken-IPv6
                            // blackhole (every attempt times out) it would stretch the
                            // fail-fast path by a trailing ~16s wait before persisting.
                            if (attempt + 1 < _retryPolicy.MaxRetries)
                            {
                                float delay = _retryPolicy.GetRetryDelaySeconds(attempt);
                                // MaxRetries counts total attempts (loop bound), so the
                                // retry budget is MaxRetries - 1.
                                Debug.LogWarning($"[Framedash] Retry {attempt + 1}/{_retryPolicy.MaxRetries - 1} in {delay:F1}s (HTTP {responseCode})");
                                // Real-time wait so an app pause / Time.timeScale == 0 does not
                                // stall retry backoff (WaitForSeconds is scaled by Time.timeScale).
                                yield return new WaitForSecondsRealtime(delay);
                            }
                            break;
                    }
                }
            }

            // Retries exhausted: nothing delivered (DeliveredLeadingCount stays 0) so the
            // caller persists the batch instead of dropping it.
            Debug.LogWarning($"[Framedash] Failed to send batch after {_retryPolicy.MaxRetries} attempts. Persisting {events.Length} event(s) for a later run.");
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

#if !UNITY_WEBGL
        // Resolve (once, lazily) the endpoint to concrete IP literals and build the
        // prefer-IPv4-with-IPv6-fallback delivery plan, then cache it (fixed endpoint +
        // stable anycast DNS). Resolution runs on the thread pool via Task.Run (the
        // coroutine polls the task per frame with a hard cap), so a slow/cold DNS
        // lookup never blocks the main thread. A resolution failure/timeout yields a
        // passthrough plan that is NOT cached as final, so a transient DNS failure
        // does not permanently disable the fallback.
        private IEnumerator EnsureDeliveryPlan()
        {
            if (_planCacheFinal && _plan != null) yield break;

            if (!EndpointAddressPlanner.ShouldForceAddressFamily(_endpointUrl))
            {
                // Structural passthrough (loopback / IP-literal / non-HTTPS endpoint):
                // deterministic for a fixed endpoint, so build and cache it once.
                _plan = EndpointAddressPlanner.Build(_endpointUrl, null, null);
                _planCacheFinal = true;
                yield break;
            }

            // Reuse the in-flight resolve from an earlier flush if there is one
            // (see _resolveTask); otherwise start a new one. Dns.GetHostAddresses
            // takes no cancellation token, so an over-cap resolve cannot be aborted,
            // only re-polled -- sharing the task caps the leak at one worker total.
            if (_resolveTask == null)
            {
                try
                {
                    // ShouldForceAddressFamily already validated the URL as an
                    // absolute HTTPS URI with a DNS host, so this cannot throw.
                    string host = new Uri(_endpointUrl).Host;
                    // One Dns call returns BOTH families (A + AAAA);
                    // ResolveBothBlocking never throws (failures degrade to a
                    // passthrough plan).
                    _resolveTask = Task.Run(() => ResolveBothBlocking(host));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Framedash] DNS resolve dispatch failed: {e.Message}");
                    _plan = EndpointAddressPlanner.Build(_endpointUrl, null, null);
                    // Non-final: a later flush retries resolution.
                    yield break;
                }
            }

            var resolveTask = _resolveTask;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!resolveTask.IsCompleted && elapsed.Elapsed.TotalSeconds < ResolveTimeoutSeconds)
            {
                yield return null;
            }

            string ipv4 = string.Empty, ipv6 = string.Empty;
            if (resolveTask.IsCompleted)
            {
                if (resolveTask.Status == TaskStatus.RanToCompletion)
                {
                    (ipv4, ipv6) = resolveTask.Result;
                }
                // Consumed (either outcome): clear so a FAILED resolution gets a
                // fresh DNS attempt on a later flush instead of replaying a stale
                // failure forever.
                _resolveTask = null;
            }
            // else: still running past the cap -- KEEP _resolveTask so the next flush
            // re-polls this same task rather than stacking another DNS worker. Build
            // a passthrough plan for THIS flush; non-final so a later flush retries.

            _plan = EndpointAddressPlanner.Build(_endpointUrl, ipv4, ipv6);
            _planCacheFinal = !_plan.IsPassthrough;
        }

        // Blocking DNS on a thread-pool thread. Returns the first address of each
        // family (empty when a family did not resolve). Never throws (fail-safe).
        private static ValueTuple<string, string> ResolveBothBlocking(string host)
        {
            try
            {
                var addresses = Dns.GetHostAddresses(host);
                string ipv4 = string.Empty, ipv6 = string.Empty;
                foreach (var address in addresses)
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork && ipv4.Length == 0)
                    {
                        ipv4 = address.ToString();
                    }
                    else if (address.AddressFamily == AddressFamily.InterNetworkV6 && ipv6.Length == 0)
                    {
                        ipv6 = address.ToString();
                    }
                }
                return (ipv4, ipv6);
            }
            catch
            {
                return (string.Empty, string.Empty);
            }
        }

        // Direct-socket TLS fallback for one attempt: POST the already-built payload
        // to the currently-selected family's IP literal. All socket/TLS work happens
        // on the thread pool (DirectSocketSender); this coroutine only polls the task
        // with a hard bound so a wedged socket can never stall the flush beyond the
        // same 10s budget the primary path has. result.StatusCode is 0 for any
        // transport-level failure, mirroring UnityWebRequest.responseCode.
        private IEnumerator SendViaDirectSocket(byte[] payload, int familyIndex, FallbackResult result)
        {
            result.StatusCode = 0;

            Task<long> sendTask;
            CancellationTokenSource abandonSource;
            try
            {
                string attemptUrl = _plan.AttemptUrls[familyIndex];
                var uri = new Uri(attemptUrl);
                byte[] head = RawHttpMessage.BuildPostHead(
                    uri.PathAndQuery, _plan.HostHeader, _apiKey, _sdkVersion, payload.Length);
                // The abandon token guarantees a Task.Run still queued behind a busy
                // thread pool (or not yet past the request write) can NEVER fire the
                // POST after this coroutine stops waiting -- a late duplicate send
                // would re-deliver events the caller already classified as failed
                // and persisted (DeliveredLeadingCount divergence).
                abandonSource = new CancellationTokenSource();
                sendTask = DirectSocketSender.PostAsync(
                    attemptUrl, _plan.CommonName, head, payload, RequestTimeoutSeconds,
                    abandonSource.Token);
            }
            catch (Exception e)
            {
                // Defensive: PostAsync itself only wraps Task.Run and should not
                // throw; treat any dispatch failure as a transport-level failure.
                Debug.LogWarning($"[Framedash] Direct-socket fallback dispatch failed: {e.Message}");
                yield break;
            }

            try
            {
                // The sender's internal timeout (RequestTimeoutSeconds) normally
                // completes the task first; the +2s polling margin is a last-resort
                // bound so a pathological thread-pool stall still cannot wedge the
                // coroutine.
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (!sendTask.IsCompleted && elapsed.Elapsed.TotalSeconds < RequestTimeoutSeconds + 2)
                {
                    yield return null;
                }

                if (sendTask.Status == TaskStatus.RanToCompletion)
                {
                    result.StatusCode = sendTask.Result;
                }
            }
            finally
            {
                // Signal abandon whether we timed out, completed, or the coroutine
                // was torn down mid-poll (iterator Dispose runs this finally): after
                // completion the cancel is a no-op; otherwise it stops an unfired or
                // pre-write send. The CTS is deliberately NOT disposed here -- the
                // still-running task may be about to link against the token, and a
                // timer-less CTS is reclaimed by GC without Dispose.
                try { abandonSource.Cancel(); }
                catch { /* never throw out of the SDK */ }
            }
        }
#endif

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
