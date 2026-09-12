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

    public sealed class TransportLayer
    {
        private sealed class SendAttemptResult
        {
            public RetryAction Action;
            public long StatusCode;
            public string FailureDetail;
        }

        private sealed class FallbackState
        {
            public int FamilyIndex;
            public bool PlanResolveAttempted;
        }

        private readonly string _endpointUrl;
        private readonly string _apiKey;
        private readonly string _sdkVersion;
        private readonly int _maxPayloadBytes;
        private readonly RetryPolicy _retryPolicy;
        private readonly bool _disabled;

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
        /// A RESOLUTION-FAILED passthrough (the endpoint qualifies but neither family resolved
        /// in time) leaves this false so a later flush retries resolution and a transient
        /// startup DNS failure does not permanently disable the fallback.
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

        /// <summary>
        /// The UnityWebRequest of the currently-executing SendBatch attempt, tracked so
        /// <see cref="AbortInFlightRequest"/> can release it after the owning coroutine is
        /// stopped. Unity disposes a STOPPED coroutine's own enumerator (running its
        /// finally -- Shutdown relies on that for persistence), but does NOT guarantee
        /// disposing a NESTED enumerator the coroutine was yielding on, so the request
        /// attempt's finally may never run and native resources would wait on the finalizer.
        /// Main-thread only (set/cleared inside the coroutine, read after
        /// StopCoroutine on the same thread).
        /// </summary>
        private UnityWebRequest _activeRequest;

        public void AbortInFlightRequest()
        {
            var request = _activeRequest;
            _activeRequest = null;
            if (request == null) return;
            // Abort is a no-op on a finished request; Dispose releases the native
            // upload/download handlers immediately instead of waiting for the finalizer.
            try { request.Abort(); } catch {   }
            try { request.Dispose(); } catch {   }
        }

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
        /// Reports how many leading events were delivered via `result` so the caller can
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

            if (!TrySerializeBatch(events, result, out byte[] payload)) yield break;

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

            yield return SendPayloadWithRetries(events, payload, result);
        }

        private static bool TrySerializeBatch(
            TelemetryEvent[] events,
            DeliveryResult result,
            out byte[] payload)
        {
            try
            {
                payload = Compress(TelemetrySerializer.Serialize(events));
                return true;
            }
            catch (Exception e)
            {
                // Deterministic poison payloads must not wedge the offline queue.
                Debug.LogError($"[Framedash] Serialization failed: {e.Message}");
                result.DeliveredLeadingCount = events.Length;
                payload = Array.Empty<byte>();
                return false;
            }
        }

        private IEnumerator SendPayloadWithRetries(
            TelemetryEvent[] events,
            byte[] payload,
            DeliveryResult result)
        {
            var fallbackState = new FallbackState();
            // MaxRetries bounds primary UnityWebRequest attempts. A transport failure may
            // add one direct-socket fallback inside that attempt, never a second retry slot.
            for (int attempt = 0; attempt < _retryPolicy.MaxRetries; attempt++)
            {
                var attemptResult = new SendAttemptResult();
                yield return ExecuteRequestAttempt(
                    events.Length,
                    payload,
                    attempt,
                    fallbackState,
                    attemptResult);

                switch (attemptResult.Action)
                {
                    case RetryAction.Success:
                        if (VerboseLogging)
                            Debug.Log(TransportLog.FormatFlushSuccess(events.Length, attemptResult.StatusCode));
                        result.DeliveredLeadingCount = events.Length;
                        yield break;

                    case RetryAction.SplitBatch:
                        yield return SplitAndResend(events, result);
                        yield break;

                    case RetryAction.Fail:
                        // Permanent failures are handled so poison events are not persisted.
                        Debug.LogWarning($"[Framedash] Send failed permanently (HTTP {attemptResult.StatusCode}); dropping {events.Length} event(s): {attemptResult.FailureDetail}");
                        result.DeliveredLeadingCount = events.Length;
                        yield break;

                    case RetryAction.Retry:
                        if (attempt + 1 < _retryPolicy.MaxRetries)
                        {
                            float delay = _retryPolicy.GetRetryDelaySeconds(attempt);
                            Debug.LogWarning($"[Framedash] Retry {attempt + 1}/{_retryPolicy.MaxRetries - 1} in {delay:F1}s (HTTP {attemptResult.StatusCode})");
                            // Unscaled time keeps retries progressing while the game is paused.
                            yield return new WaitForSecondsRealtime(delay);
                        }
                        break;
                }
            }

            // Transient exhaustion remains undelivered so the caller persists the batch.
            Debug.LogWarning($"[Framedash] Failed to send batch after {_retryPolicy.MaxRetries} attempts. Persisting {events.Length} event(s) for a later run.");
        }

        private IEnumerator ExecuteRequestAttempt(
            int eventCount,
            byte[] payload,
            int attempt,
            FallbackState fallbackState,
            SendAttemptResult result)
        {
            var request = new UnityWebRequest(_endpointUrl, "POST");
            _activeRequest = request;
            try
            {
                ConfigureRequest(request, payload);
                yield return request.SendWebRequest();

                long responseCode = request.responseCode;
                bool usedFallback = false;
#if !UNITY_WEBGL
                if (responseCode == 0)
                {
                    if (!fallbackState.PlanResolveAttempted)
                    {
                        fallbackState.PlanResolveAttempted = true;
                        yield return EnsureDeliveryPlan();
                    }
                    if (_plan != null && !_plan.IsPassthrough)
                    {
                        if (VerboseLogging)
                            Debug.Log($"[Framedash] Transport-level failure on primary connect; direct-socket fallback to {_plan.AttemptUrls[fallbackState.FamilyIndex]}");
                        var fallback = new FallbackResult();
                        yield return SendViaDirectSocket(payload, fallbackState.FamilyIndex, fallback);
                        responseCode = fallback.StatusCode;
                        usedFallback = true;
                        if (responseCode == 0)
                        {
                            fallbackState.FamilyIndex = EndpointAddressPlanner.NextFamily(
                                fallbackState.FamilyIndex,
                                _plan.AttemptUrls.Count);
                        }
                    }
                }
#endif

                result.StatusCode = responseCode;
                result.Action = _retryPolicy.Classify(responseCode, attempt, eventCount);
                if (result.Action == RetryAction.Fail)
                {
                    result.FailureDetail = usedFallback
                        ? "(direct-socket fallback; no response body captured)"
                        : request.downloadHandler.text;
                }
            }
            finally
            {
                // A stopped parent coroutine can release the same request through AbortInFlightRequest.
                _activeRequest = null;
                request.Dispose();
            }
        }

        private void ConfigureRequest(UnityWebRequest request, byte[] payload)
        {
            request.uploadHandler = new UploadHandlerRaw(payload);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = RequestTimeoutSeconds;
            request.redirectLimit = 0;
            request.SetRequestHeader("Content-Type", "application/x-protobuf");
            request.SetRequestHeader("Content-Encoding", "gzip");
            request.SetRequestHeader("X-API-Key", _apiKey);
            request.SetRequestHeader("X-SDK-Version", _sdkVersion);
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
        // Resolution runs on the thread pool via Task.Run (the coroutine polls the task per
        // frame with a hard cap), so a slow/cold DNS lookup never blocks the main thread. A
        // resolution failure/timeout yields a passthrough plan that is NOT cached as final, so
        // a transient DNS failure does not permanently disable the fallback.
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
                    _resolveTask = Task.Run(() => ResolveBothBlocking(host));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Framedash] DNS resolve dispatch failed: {e.Message}");
                    _plan = EndpointAddressPlanner.Build(_endpointUrl, null, null);
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
                catch {   }
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
