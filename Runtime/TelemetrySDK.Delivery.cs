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
                if (!_initialized || (_buffer.Count == 0 && _inFlightBatch == null && _retainedBlockingBatch == null))
                {
                    Interlocked.Exchange(ref _isFlushing, 0);
                    return;
                }
                // Reset _flushRequested AFTER the _isFlushing guard so a
                // background-thread request arriving between the two checks
                // is not silently dropped.
                bool retainedFromBlocking = _inFlightBatch != null || _retainedBlockingBatch != null;
                _flushRequested = false;
                if (!retainedFromBlocking) Interlocked.Exchange(ref _estimatedPayloadBytes, 0);

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

                TelemetryEvent[] batch = _inFlightBatch ?? _retainedBlockingBatch ?? _buffer.DequeueAll();
                if (ReferenceEquals(batch, _retainedBlockingBatch))
                {
                    _retainedBlockingBatch = null;
                    _inFlightPersistedCount = 0;
                }
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
                    if ((retainInMemory || !_initialized) && !_offlineQueueActive && result.DeliveredLeadingCount < events.Length)
                    {
                        _inFlightBatch = UndeliveredTail(events, result.DeliveredLeadingCount);
                        _inFlightPersistedCount = 0;
                    }
                    else
                    {
                        ApplyPersistenceResult(events, persistedCount, result.DeliveredLeadingCount, out _inFlightBatch);
                        _inFlightPersistedCount = 0;
                    }
                    if (retainInMemory && _inFlightBatch == null && _initialized
                        && (_buffer.Count > 0 || _retainedBlockingBatch != null))
                        _flushRequested = true;
                    _inFlightFlush = null;
                    Interlocked.Exchange(ref _isFlushing, 0);
                }
            }
        }

        // Re-appending an undelivered persisted prefix would duplicate it on the next run.
        private bool ApplyPersistenceResult(TelemetryEvent[] events, int persistedCount, int deliveredLeadingCount,
            out TelemetryEvent[] unpersisted)
        {
            unpersisted = null;
            if (!_offlineQueueActive) return true;
            int ackCount = Math.Min(persistedCount, deliveredLeadingCount);
            bool persistenceOk = ackCount == 0 || !_persistenceAcknowledgementFailed;
            int persistStart = Math.Max(deliveredLeadingCount, persistedCount);
            TelemetryEvent[] fresh = persistStart < events.Length ? UndeliveredTail(events, persistStart) : null;
            try
            {
                // A later positional ack cannot skip an earlier prefix that failed removal.
                if (ackCount > 0 && persistenceOk && !_persistence.DropOldest(ackCount))
                {
                    _persistenceAcknowledgementFailed = true;
                    persistenceOk = false;
                    Debug.LogWarning("[Framedash] Offline queue acknowledgement failed; positional acknowledgements are paused until reinitialization. Delivered events may replay.");
                }

                if (fresh != null && !_persistence.Append(fresh))
                {
                    persistenceOk = false;
                    unpersisted = fresh;
                    Debug.LogWarning($"[Framedash] Offline queue write failed; retaining {fresh.Length} event(s) outside the producer ring for retry.");
                }
                return persistenceOk;
            }
            catch (Exception e)
            {
                if (ackCount > 0) _persistenceAcknowledgementFailed = true;
                unpersisted = fresh;
                Debug.LogError($"[Framedash] Offline queue update failed: {e}");
                return false;
            }
        }

        /// <summary>
        /// Coroutine delivery cannot advance while the main thread is blocked. A bounded
        /// synchronous attempt can run before exit, but HTTP acknowledgement does not prove
        /// durable ingestion and disabled persistence cannot retain memory after process exit.
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

                // Configuration can change while initialized; this endpoint belongs to
                // the active credential, unlike the pending configured URL.
                if (!EndpointSecurity.IsEndpointTransportSecure(_transport.EndpointUrl)) return false;

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
        // Merging envelopes changes the consumer's hash and can duplicate an uncertain send.
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

            bool producerDeferred = _retainedBlockingBatch != null;
            if (!producerDeferred)
            {
                // Preserve signals from producers admitted after this snapshot.
                _flushRequested = false;
                Interlocked.Exchange(ref _estimatedPayloadBytes, 0);
            }
            // Retrying before another dequeue bounds retained memory to two envelopes.
            TelemetryEvent[] buffered = _retainedBlockingBatch ?? _buffer.DequeueAll();
            _retainedBlockingBatch = null;
            // A live in-flight batch already carried the ENTIRE persisted prefix when the
            // earlier Flush dequeued it, so bufferedPersisted is 0 whenever inFlight is
            // present; the two prefixes are never both non-zero.
            int bufferedPersisted = producerDeferred ? 0 : Math.Min(_pendingPersistedEventsToAck, buffered.Length);
            _pendingPersistedEventsToAck -= bufferedPersisted;

            TelemetryEvent[][] envelopes = BatchPolicy.BuildBlockingEnvelopes(buffered, inFlight);
            if (envelopes.Length == 0) return true;

            var sender = new BlockingHttpSender(_transport.EndpointUrl, _effectiveApiKey, SdkVersion, _transport.BlockingDnsResolver);
            Func<long> clock = () => elapsed.ElapsedMilliseconds;
            BlockingFlush.BlockingPost post = payload =>
            {
                long remaining = budgetMs - elapsed.ElapsedMilliseconds;
                return remaining <= 0 ? 0 : sender.Post(payload, remaining);
            };

            int deliveredBuffered = 0;
            int deliveredInFlight = 0;
            // Strict budget contract: a 2xx confirmed at/after the deadline still ACKS its
            // events (delivered counts include them, so they are DropOldest'd / not
            // re-persisted -- never resent, no duplicate), but the flush as a whole reports
            // false because the confirmation missed the budget.
            bool withinBudget = true;
            foreach (TelemetryEvent[] envelope in envelopes)
            {
                // Preserve an async envelope's accepted shape for deduplication; blocking
                // snapshots use deterministic splits to bound serialization work.
                bool isInFlight = ReferenceEquals(envelope, inFlight);
                int delivered = BlockingFlush.SendLeading(
                    envelope, _maxPayloadBytes, clock, budgetMs, post,
                    allowChunkBytesSplit: !isInFlight, out bool envelopeWithinBudget);
                if (!envelopeWithinBudget) withinBudget = false;
                if (isInFlight) deliveredInFlight = delivered; else deliveredBuffered = delivered;
            }

            // Reconcile the older prefix before appending: a bounded append can evict the
            // disk head and make a later positional acknowledgement delete the wrong events.
            bool allDelivered = true;
            if (inFlight != null && inFlight.Length > 0)
            {
                if (!ReconcileBlockingDelivery(inFlight, inFlightPersisted, deliveredInFlight, out _inFlightBatch))
                    allDelivered = false;
                _inFlightPersistedCount = 0;
            }
            if (buffered.Length > 0
                && !ReconcileBlockingEnvelope(buffered, bufferedPersisted, deliveredBuffered))
            {
                allDelivered = false;
            }

            if (producerDeferred && _buffer.Count > 0)
            {
                _flushRequested = true;
                allDelivered = false;
            }
            if (allDelivered && _verboseLogging)
                Debug.Log($"[Framedash] FlushBlocking delivered {deliveredInFlight + deliveredBuffered} event(s).");
            // A late-confirmed delivery was still acked above (no resend, no duplicate);
            // the false return only reports that confirmation missed the budget.
            return allDelivered && withinBudget;
        }

        // Merging envelopes would change the consumer's dedup token.
        private bool ReconcileBlockingEnvelope(TelemetryEvent[] events, int persistedCount, int delivered)
        {
            return ReconcileBlockingDelivery(events, persistedCount, delivered, out _retainedBlockingBatch);
        }

        private bool ReconcileBlockingDelivery(TelemetryEvent[] events, int persistedCount, int delivered,
            out TelemetryEvent[] retained)
        {
            retained = null;
            if (events == null || events.Length == 0) return true;
            bool persisted = true;
            if (_offlineQueueActive)
            {
                persisted = ApplyPersistenceResult(events, persistedCount, delivered, out retained);
            }
            else if (delivered < events.Length)
            {
                // Producers can refill the ring while the snapshot is being sent.
                retained = UndeliveredTail(events, delivered);
            }
            if (retained != null) _flushRequested = true;
            return delivered == events.Length && persisted;
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
                lock (_lifecycleGate)
                {
                    if (!_initialized) return;
                    if (_flushCoroutine != null) StopCoroutine(_flushCoroutine);
                    EndPerformanceRun(completed: false);
                    // A normal send's finalizer must retain its tail when stopped during shutdown.
                    _initialized = false;
                    // Stopping a send must not clear its tail before final reconciliation.
                    if (_inFlightFlush != null)
                    {
                        StopCoroutine(_inFlightFlush);
                        _inFlightFlush = null;
                        // Unity may leave yielded transport iterators alive after stopping the owner.
                        _transport.AbortInFlightRequest();
                    }
                    _flushGeneration++;
                    if (_offlineQueueActive)
                    {
                        // Stopped iterators may not finalize; reconcile any remaining owned envelope.
                        if (_inFlightBatch != null)
                            ApplyPersistenceResult(_inFlightBatch, _inFlightPersistedCount, 0, out _inFlightBatch);
                        _inFlightPersistedCount = 0;
                        if (_retainedBlockingBatch != null)
                            ApplyPersistenceResult(_retainedBlockingBatch, 0, 0, out _retainedBlockingBatch);
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
                        var envelopes = new System.Collections.Generic.List<TelemetryEvent[]>(3);
                        var buffered = _buffer.DequeueAll();
                        if (buffered.Length > 0) envelopes.Add(buffered);
                        if (_retainedBlockingBatch != null) envelopes.Add(_retainedBlockingBatch);
                        if (_inFlightBatch != null) envelopes.Add(_inFlightBatch);
                        _retainedBlockingBatch = null;
                        if (envelopes.Count > 0)
                        {
                            Interlocked.Exchange(ref _isFlushing, 1);
                            _inFlightBatch = envelopes[0];
                            _inFlightFlush = StartCoroutine(FlushShutdownEnvelopes(envelopes.ToArray(), _flushGeneration));
                        }
                    }
                    Debug.Log("[Framedash] SDK shut down.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] Shutdown() failed: {e}");
            }
        }

        private IEnumerator FlushShutdownEnvelopes(TelemetryEvent[][] envelopes, int generation)
        {
            try
            {
                // Capture both envelopes before disabling the SDK; Flush() then rejects further work.
                foreach (TelemetryEvent[] events in envelopes)
                {
                    if (generation != _flushGeneration) yield break;
                    _inFlightBatch = events;
                    yield return _transport.SendBatch(events, new DeliveryResult());
                }
            }
            finally
            {
                if (generation == _flushGeneration)
                {
                    _inFlightFlush = null;
                    _inFlightBatch = null;
                    Interlocked.Exchange(ref _isFlushing, 0);
                }
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
