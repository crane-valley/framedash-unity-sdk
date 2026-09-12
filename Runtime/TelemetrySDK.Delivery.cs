using System;
using System.Collections;
using System.Threading;
using UnityEngine;

namespace Framedash
{
    public sealed partial class TelemetrySDK : MonoBehaviour
    {
        public void Flush()
        {
            try
            {
                if (Interlocked.CompareExchange(ref _isFlushing, 1, 0) != 0) return;
                if (!_initialized || (_buffer.Count == 0 && _inFlightBatch == null))
                {
                    Interlocked.Exchange(ref _isFlushing, 0);
                    return;
                }
                // Reset _flushRequested AFTER the _isFlushing guard so a
                // background-thread request arriving between the two checks
                // is not silently dropped.
                _flushRequested = false;
                Interlocked.Exchange(ref _estimatedPayloadBytes, 0);

                // Head-alignment guard: if the ring dropped events since restore while
                // persisted events are still pending ack, the in-memory head no longer
                // lines up with the on-disk head, so a positional DropOldest could ack the
                // wrong events. Conservatively clear the queue and stop positional acking
                // (the evicted events are old telemetry shed under sustained overload).
                if (_offlineQueueActive && _pendingPersistedEventsToAck > 0
                    && _buffer.DroppedCount != _persistedDropBaseline)
                {
                    _persistence.Clear();
                    _pendingPersistedEventsToAck = 0;
                    _persistedDropBaseline = _buffer.DroppedCount;
                    Debug.LogWarning("[Framedash] Offline queue head misaligned after a buffer overflow; cleared the persisted queue to avoid acking the wrong events.");
                }

                bool retainedFromBlocking = _inFlightBatch != null;
                TelemetryEvent[] batch = _inFlightBatch ?? _buffer.DequeueAll();
                // The leading min(pendingAck, batch) events are already on disk; mark
                // them so the flush can ack (DropOldest) them on success and avoid
                // re-persisting them on failure. They leave the buffer now, so drop them
                // from the pending count (whether the send succeeds or not).
                int persistedCount = retainedFromBlocking
                    ? _inFlightPersistedCount : Math.Min(_pendingPersistedEventsToAck, batch.Length);
                if (!retainedFromBlocking) _pendingPersistedEventsToAck -= persistedCount;
                // Retain the batch + its persisted count so FlushBlocking can reclaim
                // this in-flight send (the blocked main thread cannot advance its coroutine).
                _inFlightBatch = batch;
                _inFlightPersistedCount = persistedCount;
                _inFlightFlush = StartCoroutine(FlushCoroutine(batch, _flushGeneration, persistedCount, retainedFromBlocking));
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _isFlushing, 0);
                Debug.LogError($"[Framedash] Flush() failed: {e}");
            }
        }

        private IEnumerator FlushCoroutine(TelemetryEvent[] events, int generation, int persistedCount, bool retainInMemory)
        {
            var result = new DeliveryResult();
            try
            {
                yield return _transport.SendBatch(events, result);
            }
            finally
            {
                // A re-init (Shutdown then Initialize) makes this flush stale: a newer
                // session now owns the offline queue and the single-flight guard. Skip
                // both the queue accounting and the guard release so the stale flush
                // cannot disturb the new session (matches the Godot FlushAsync guard and
                // UE5, whose transport AliveFlag drops a stale flush's callback).
                if (generation == _flushGeneration)
                {
                    if (retainInMemory && !_offlineQueueActive && result.DeliveredLeadingCount < events.Length)
                    {
                        _inFlightBatch = UndeliveredTail(events, result.DeliveredLeadingCount);
                        _inFlightPersistedCount = 0;
                    }
                    else
                    {
                        ApplyPersistenceResult(events, persistedCount, result.DeliveredLeadingCount);
                        _inFlightBatch = null;
                    }
                    _inFlightFlush = null;
                    Interlocked.Exchange(ref _isFlushing, 0);
                }
            }
        }

        // Reconcile the offline queue with what the transport delivered. The batch is
        // laid out as [persisted leading block | fresh tail], and the transport reports
        // how many leading events were delivered:
        //   - acknowledge (DropOldest) the persisted events that were delivered =
        //     min(persistedCount, deliveredLeadingCount), the leading-and-on-disk block;
        //   - persist (Append) the undelivered fresh tail = events at index >=
        //     max(deliveredLeadingCount, persistedCount) (not delivered AND not already
        //     on disk), so a transient failure keeps them for the next run.
        // Undelivered events still inside the persisted block stay on disk untouched
        // (never double-persisted). The common case (no persisted events, full delivery)
        // touches no disk at all.
        private void ApplyPersistenceResult(TelemetryEvent[] events, int persistedCount, int deliveredLeadingCount)
        {
            if (!_offlineQueueActive) return;
            try
            {
                int ackCount = Math.Min(persistedCount, deliveredLeadingCount);
                if (ackCount > 0) _persistence.DropOldest(ackCount);

                int persistStart = Math.Max(deliveredLeadingCount, persistedCount);
                if (persistStart < events.Length)
                {
                    var toPersist = new TelemetryEvent[events.Length - persistStart];
                    Array.Copy(events, persistStart, toPersist, 0, toPersist.Length);
                    if (!_persistence.Append(toPersist))
                    {
                        // Disk write failed (full / permissions): the tail was already
                        // dequeued, so re-enqueue it to the in-memory buffer to retry on a
                        // later flush rather than dropping it. These events are fresh (not
                        // on disk), so they go to the tail and do not affect the persisted
                        // leading block. The ring still bounds memory if the disk stays bad.
                        Debug.LogWarning($"[Framedash] Offline queue write failed; keeping {toPersist.Length} event(s) in memory for retry.");
                        foreach (var evt in toPersist) _buffer.Enqueue(evt);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] Offline queue update failed: {e}");
            }
        }

        /// <summary>
        /// Synchronously flush every event buffered at call time, blocking the main
        /// thread up to <paramref name="timeoutMs"/> milliseconds (clamped to 30,000),
        /// and return whether all of them were CONFIRMED delivered (HTTP 2xx) within the
        /// budget. Use it at a moment that can afford a short block -- level end, or
        /// before quit on a platform without offline storage -- to guarantee delivery
        /// instead of relying on the periodic flush + offline queue.
        ///
        /// Returns false, losing nothing, on: timeout, transport failure, the SDK not
        /// initialized or the endpoint failing the transport-security check,
        /// <paramref name="timeoutMs"/> &lt;= 0, WebGL (no sockets), or a call from a
        /// thread other than the main thread (a warning is logged; the call is NOT
        /// marshaled-and-blocked). Undelivered events stay buffered / on the offline
        /// queue, so a false return never drops telemetry.
        ///
        /// Never throws (fail-safe), never blocks meaningfully past the budget, and the
        /// SDK keeps operating normally afterward (the periodic flush resumes).
        /// </summary>
        public bool FlushBlocking(int timeoutMs)
        {
            try
            {
                // Off the main thread: refuse rather than marshal-and-block, which would
                // deadlock against the main loop the blocked caller waits on.
                if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                {
                    Debug.LogWarning("[Framedash] FlushBlocking() must be called from the main thread. Ignored.");
                    return false;
                }

                int budgetMs = BlockingFlush.ResolveBudgetMs(timeoutMs);
                if (budgetMs <= 0) return false;

                if (!_initialized) return false;

                // Fail closed: an endpoint that failed the transport-security check never
                // sends. Re-checked here (not via the transport) so the guarantee holds
                // regardless of transport state; events stay buffered (no loss).
                if (!EndpointSecurity.IsEndpointTransportSecure(_endpointUrl)) return false;

#if UNITY_WEBGL
                // WebGL has no sockets and cannot block its single thread; the async path
                // + offline queue remain the only delivery mechanism there.
                Debug.LogWarning("[Framedash] FlushBlocking() is not supported on WebGL; use Flush().");
                return false;
#else
                var elapsed = System.Diagnostics.Stopwatch.StartNew();

                // Reclaim any in-flight async flush BEFORE draining the buffer: the blocked
                // main thread cannot pump its coroutine, so its already-dequeued batch must
                // be delivered here or it is stranded. Capture the batch, then bump the
                // flush generation so the coroutine's generation-gated finally (run when
                // StopCoroutine disposes the iterator) neither re-persists the batch nor
                // releases the single-flight guard -- FlushBlocking accounts for the batch
                // itself. Mirrors Shutdown's in-flight handling, but delivers, not persists.
                TelemetryEvent[] inFlight = _inFlightBatch;
                int inFlightPersisted = _inFlightPersistedCount;
                _inFlightBatch = null;
                _flushGeneration++;
                if (_inFlightFlush != null)
                {
                    StopCoroutine(_inFlightFlush);
                    _inFlightFlush = null;
                    // Unity does not guarantee disposing the NESTED SendBatch enumerator of
                    // a stopped coroutine, so its UnityWebRequest using-block may never run;
                    // release it explicitly (no-op when it already completed/disposed).
                    _transport.AbortInFlightRequest();
                }
                // Hold the single-flight guard for the blocking send (defensive: with the
                // main thread blocked, no coroutine flush can start meanwhile anyway).
                Interlocked.Exchange(ref _isFlushing, 1);

                try
                {
                    return DrainBlocking(inFlight, inFlightPersisted, elapsed, budgetMs);
                }
                finally
                {
                    // Release the single-flight guard so the periodic FlushLoop resumes. Do
                    // NOT clear _flushRequested / _estimatedPayloadBytes here: a background
                    // Track() during the block may have enqueued post-snapshot events and set
                    // those signals, and erasing them would delay those events to the next
                    // periodic interval. DrainBlocking resets them BEFORE its dequeue (like
                    // Flush), so only genuinely post-snapshot signals survive.
                    Interlocked.Exchange(ref _isFlushing, 0);
                }
#endif
            }
            catch (Exception e)
            {
                // Never throw out of a public method; release the guard defensively in case
                // the failure happened after it was taken.
                Interlocked.Exchange(ref _isFlushing, 0);
                Debug.LogError($"[Framedash] FlushBlocking() failed: {e}");
                return false;
            }
        }

#if !UNITY_WEBGL
        // Deliver the at-call-time events synchronously within the shared budget, then
        // reconcile the offline-queue persisted prefix. The reclaimed in-flight batch and
        // the freshly-buffered events are sent as SEPARATE envelopes -- NEVER concatenated:
        // the consumer dedups by hashing the full ordered event array, so re-sending the
        // in-flight batch with its ORIGINAL array keeps the same dedup token (dropped if it
        // already reached ingest), whereas an "in-flight + buffered" array would hash
        // differently and double-insert/charge every in-flight event. Buffered goes FIRST
        // (guaranteed-undelivered) so a wedged endpoint under the shared budget cannot
        // starve it behind a possibly-redundant in-flight resend. Returns true only when
        // EVERY envelope was fully delivered; undelivered events are never lost (persisted
        // with the offline queue on, re-buffered with it off).
        private bool DrainBlocking(TelemetryEvent[] inFlight, int inFlightPersisted,
            System.Diagnostics.Stopwatch elapsed, int budgetMs)
        {
            // Head-alignment guard (same as Flush): a ring overflow since restore desyncs
            // the in-memory head from the on-disk head, so positional acking is unsafe --
            // conservatively clear the persisted queue.
            if (_offlineQueueActive && _pendingPersistedEventsToAck > 0
                && _buffer.DroppedCount != _persistedDropBaseline)
            {
                _persistence.Clear();
                _pendingPersistedEventsToAck = 0;
                _persistedDropBaseline = _buffer.DroppedCount;
                Debug.LogWarning("[Framedash] Offline queue head misaligned after a buffer overflow; cleared the persisted queue to avoid acking the wrong events.");
            }

            // Reset the flush signals BEFORE dequeuing (mirrors Flush): a background Track()
            // that enqueues an event AFTER this point keeps its own _flushRequested / payload
            // estimate, so a post-snapshot event still triggers a count/size flush instead of
            // waiting for the periodic interval.
            _flushRequested = false;
            Interlocked.Exchange(ref _estimatedPayloadBytes, 0);

            TelemetryEvent[] buffered = _buffer.DequeueAll();
            // A live in-flight batch already carried the ENTIRE persisted prefix when the
            // earlier Flush dequeued it, so bufferedPersisted is 0 whenever inFlight is
            // present; the two prefixes are never both non-zero.
            int bufferedPersisted = Math.Min(_pendingPersistedEventsToAck, buffered.Length);
            _pendingPersistedEventsToAck -= bufferedPersisted;

            TelemetryEvent[][] envelopes = BatchPolicy.BuildBlockingEnvelopes(buffered, inFlight);
            if (envelopes.Length == 0) return true;

            var sender = new BlockingHttpSender(_endpointUrl, _effectiveApiKey, SdkVersion, _transport.BlockingDnsResolver);
            Func<long> clock = () => elapsed.ElapsedMilliseconds;
            BlockingFlush.BlockingPost post = payload =>
            {
                long remaining = budgetMs - elapsed.ElapsedMilliseconds;
                return remaining <= 0 ? 0 : sender.Post(payload, remaining);
            };

            // Phase 1 -- SEND every envelope in BuildBlockingEnvelopes order (buffered FIRST,
            // so the guaranteed-undelivered buffered events get budget priority over a
            // possibly-redundant in-flight resend). Persistence is DEFERRED to phase 2.
            int deliveredBuffered = 0;
            int deliveredInFlight = 0;
            // Strict budget contract: a 2xx confirmed at/after the deadline still ACKS its
            // events (delivered counts include them, so they are DropOldest'd / not
            // re-persisted -- never resent, no duplicate), but the flush as a whole reports
            // false because the confirmation missed the budget.
            bool withinBudget = true;
            foreach (TelemetryEvent[] envelope in envelopes)
            {
                // The reclaimed in-flight envelope keeps its identical array shape (its size
                // was already accepted by the parked async send, so the pre-serialize
                // size-split is disabled) to preserve the consumer's per-POST dedup token;
                // the freshly-buffered envelope has no async owner and IS pre-split so no
                // single serialize+gzip overruns the budget.
                bool isInFlight = ReferenceEquals(envelope, inFlight);
                int delivered = BlockingFlush.SendLeading(
                    envelope, _maxPayloadBytes, clock, budgetMs, post,
                    allowChunkBytesSplit: !isInFlight, out bool envelopeWithinBudget);
                if (!envelopeWithinBudget) withinBudget = false;
                if (isInFlight) deliveredInFlight = delivered; else deliveredBuffered = delivered;
            }

            // Phase 2 -- RECONCILE the IN-FLIGHT envelope FIRST. Its ApplyPersistenceResult is
            // the only one that positionally ACKs (DropOldest) the persisted-queue head (the
            // restored prefix). The buffered envelope never carries a persisted prefix while an
            // in-flight batch exists (bufferedPersisted == 0), so it only ever APPENDS its
            // undelivered tail -- and an Append can EVICT the queue head once MaxPersistedEvents
            // is exceeded. Acking first, on the UNSHIFTED queue, keeps the positional-ack
            // invariant (#1317) in every {in-flight}x{buffered} success/fail interleaving:
            // otherwise a buffered failure-append could shift the head so the in-flight ack
            // deletes the just-appended undelivered events instead of the delivered prefix.
            bool allDelivered = true;
            if (inFlight != null && inFlight.Length > 0)
            {
                if (!_offlineQueueActive && deliveredInFlight < inFlight.Length)
                {
                    // Both envelopes can fill the ring; retain the reclaimed envelope separately.
                    _inFlightBatch = UndeliveredTail(inFlight, deliveredInFlight);
                    _inFlightPersistedCount = 0;
                    _flushRequested = true;
                    allDelivered = false;
                }
                else if (!ReconcileBlockingEnvelope(inFlight, inFlightPersisted, deliveredInFlight))
                    allDelivered = false;
            }
            if (buffered.Length > 0
                && !ReconcileBlockingEnvelope(buffered, bufferedPersisted, deliveredBuffered))
            {
                allDelivered = false;
            }

            if (allDelivered && _verboseLogging)
                Debug.Log($"[Framedash] FlushBlocking delivered {deliveredInFlight + deliveredBuffered} event(s).");
            // A late-confirmed delivery was still acked above (no resend, no duplicate);
            // the false return only reports that confirmation missed the budget.
            return allDelivered && withinBudget;
        }

        // Reconcile ONE independent blocking-flush envelope: ack its delivered persisted
        // prefix (DropOldest) and keep the undelivered remainder -- persisted with the
        // offline queue on (ApplyPersistenceResult), re-buffered with it off so nothing is
        // lost. Envelopes are never merged (that would change the consumer's dedup token).
        // Returns whether the whole envelope was confirmed delivered.
        private bool ReconcileBlockingEnvelope(TelemetryEvent[] events, int persistedCount, int delivered)
        {
            if (events == null || events.Length == 0) return true;
            if (_offlineQueueActive)
            {
                ApplyPersistenceResult(events, persistedCount, delivered);
            }
            else if (delivered < events.Length)
            {
                // No offline queue: keep the undelivered tail in memory (buffer tail) so a
                // failed/timed-out blocking flush loses nothing, and nudge a flush so the
                // periodic loop retries promptly.
                for (int i = delivered; i < events.Length; i++) _buffer.Enqueue(events[i]);
                _flushRequested = true;
            }
            return delivered == events.Length;
        }
#endif

        private static TelemetryEvent[] UndeliveredTail(TelemetryEvent[] events, int delivered)
        {
            if (delivered <= 0) return events;
            if (delivered >= events.Length) return Array.Empty<TelemetryEvent>();
            var tail = new TelemetryEvent[events.Length - delivered];
            Array.Copy(events, delivered, tail, 0, tail.Length);
            return tail;
        }

        public void Shutdown()
        {
            try
            {
                if (!_initialized) return;
                if (_flushCoroutine != null) StopCoroutine(_flushCoroutine);
                EndPerformanceRun(completed: false);
                // Stop the in-flight send (if any) so its generation-gated finally runs now
                // and persists the batch it had already dequeued -- otherwise those events,
                // which are no longer in _buffer, would be lost on quit. Stopping a finished
                // coroutine is a no-op. The finally sees DeliveredLeadingCount unset (0) for
                // an interrupted send, so it persists the whole undelivered tail.
                if (_inFlightFlush != null)
                {
                    StopCoroutine(_inFlightFlush);
                    _inFlightFlush = null;
                    _inFlightBatch = null;
                    // Same nested-enumerator dispose gap as the FlushBlocking reclaim: the
                    // stopped coroutine's own finally runs (persisting the batch), but its
                    // yielded SendBatch enumerator -- and the UnityWebRequest inside -- may
                    // not be disposed by the engine. Release it explicitly.
                    _transport.AbortInFlightRequest();
                }
                if (_offlineQueueActive)
                {
                    // Persist whatever is still buffered instead of a best-effort network
                    // flush: a synchronous disk write completes before the app exits, and
                    // the offline queue resends next run. An in-flight periodic flush at
                    // this instant is not captured -- the same best-effort limitation that
                    // applies to any in-flight send on a hard exit.
                    TelemetryEvent[] remaining = _buffer.DequeueAll();
                    // Skip the leading block already on disk (restored this run and not yet
                    // flushed); appending it would double-persist those events and resend
                    // them twice next run. Only the fresh tail needs persisting.
                    int alreadyPersisted = Math.Min(_pendingPersistedEventsToAck, remaining.Length);
                    int freshCount = remaining.Length - alreadyPersisted;
                    if (freshCount > 0)
                    {
                        var fresh = new TelemetryEvent[freshCount];
                        Array.Copy(remaining, alreadyPersisted, fresh, 0, freshCount);
                        if (_persistence.Append(fresh))
                            Debug.Log($"[Framedash] Shutdown: persisted {freshCount} buffered event(s) for next run.");
                        else
                            Debug.LogWarning($"[Framedash] Shutdown: {freshCount} buffered event(s) could not be persisted.");
                    }
                }
                else
                {
                    Flush();
                }
                _initialized = false;
                Debug.Log("[Framedash] SDK shut down.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] Shutdown() failed: {e}");
            }
        }

        private IEnumerator FlushLoop()
        {
            float lastFlushTime = Time.realtimeSinceStartup;
            while (true)
            {
                // Poll each frame up to the flush interval (capped at 1s) and
                // break early when Track() sets _flushRequested.
                // Per-frame bool check is negligible; battery cost comes from network I/O.
                float pollWindow = Mathf.Min(_flushPolicy.FlushIntervalSeconds, 1.0f);
                float waitStart = Time.realtimeSinceStartup;
                while (!_flushRequested && (Time.realtimeSinceStartup - waitStart) < pollWindow)
                {
                    yield return null;
                }
                float elapsed = Time.realtimeSinceStartup - lastFlushTime;
                if (_flushPolicy.ShouldFlush(_flushRequested, elapsed))
                {
                    lastFlushTime = Time.realtimeSinceStartup;
                    Flush();
                }
                // Guarantee at least one yield per outer iteration to prevent
                // tight-spinning when _flushRequested stays set (e.g. a flush
                // is already in progress so Flush() returns without clearing it).
                yield return null;
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            // Wrap so no exception escapes the engine callback (the NEVER-throw hard rule).
            try
            {
                if (pauseStatus) Flush();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] OnApplicationPause() failed: {e}");
            }
        }

        private void OnApplicationQuit()
        {
            // Wrap so no exception escapes the engine callback (the NEVER-throw hard rule).
            try
            {
                Shutdown();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] OnApplicationQuit() failed: {e}");
            }
        }

#if UNITY_EDITOR
        // Propagate an Inspector edit of _verboseLogging to the live transport. Unity
        // writes a [SerializeField] private field directly (bypassing the VerboseLogging
        // property setter), so without this an in-editor Play Mode toggle would not reach
        // _transport. Editor-only (OnValidate never runs in a build) and no-op before init.
        private void OnValidate()
        {
            if (_transport != null) _transport.VerboseLogging = _verboseLogging;
        }
#endif

        private void OnDestroy()
        {
            // Wrap so no exception escapes the engine callback (the NEVER-throw hard rule).
            try
            {
                if (s_instance == this)
                {
                    Shutdown();
                    s_instance = null;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] OnDestroy() failed: {e}");
            }
        }
    }
}
