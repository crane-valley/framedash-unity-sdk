using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Framedash
{
    public sealed partial class TelemetrySDK : MonoBehaviour
    {
        public void Track(string eventName, string mapId = "",
            Vector3? position = null, Dictionary<string, string> attributes = null,
            Dictionary<string, float> metrics = null)
        {
            try
            {
                if (!_initialized)
                {
                    Debug.LogWarning("[Framedash] SDK not initialized. Call Initialize() first.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(eventName))
                {
                    Debug.LogWarning("[Framedash] eventName must not be null, empty, or whitespace. Event dropped.");
                    return;
                }

                if (!_warnedEmptyPlayerId && string.IsNullOrEmpty(_session.PlayerId))
                {
                    _warnedEmptyPlayerId = true;
                    Debug.LogWarning("[Framedash] No player_id set. Events will be sent as anonymous. Call SetPlayerId() to associate events with a player.");
                }

                // Normalize event name first so sampling and the wire-side event use the
                // same key — overrides registered for long names must match the truncated
                // form that actually leaves the SDK and that ingest validation accepts.
                string safeEventName = FieldClamp.TruncateEventName(eventName);

                // Sampling check — skip expensive perf collection if event is dropped
                if (!_samplingPolicy.ShouldSample(safeEventName))
                    return;

                // Convert Dictionary parameters to serializable List types, enforcing the
                // ingest-core caps client-side (count, key/value length, finite metrics) so a
                // single oversized map cannot make the consumer drop the whole flush.
                List<StringPair> attrList = FieldClamp.ClampAttributes(attributes);
                List<FloatPair> metricList = FieldClamp.ClampMetrics(metrics);

                string safeMapId = FieldClamp.Truncate(mapId ?? "", FieldClamp.MaxMapIdLength);

                // Position-qualified events (non-empty map id) also carry the cached
                // mem.* reading so the spatial heatmap grid query (map_id + cell bounds)
                // sees real memory data -- perf_heartbeat alone has an empty map_id and
                // never reaches that grid. Attaches from the cache only (refreshed at
                // heartbeat cadence in TrackAutomated): no Profiler call on this per-event
                // path. A caller-supplied metric of the same key name is never clobbered.
                if (safeMapId.Length > 0)
                {
                    metricList = _memCache.AppendTo(metricList);
                }

                TrackInternal(
                    safeEventName,
                    safeMapId,
                    FieldClamp.SanitizeCoord(position?.x ?? 0f),
                    FieldClamp.SanitizeCoord(position?.y ?? 0f),
                    FieldClamp.SanitizeCoord(position?.z ?? 0f),
                    TelemetrySource.Player,
                    attrList,
                    metricList);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] Track() failed: {e}");
            }
        }

        private void TrackAutomated(string eventName)
        {
            try
            {
                // Automated events (session_start, perf_heartbeat) bypass sampling,
                // name validation, and player-ID checks — they are always valid and
                // fired from internal SDK code after initialization succeeds.
                //
                // Disk I/O and memory are BUILT on the perf_heartbeat ONLY: drain the io
                // window (folding in the automatic source delta) into the io.* metrics
                // keys, refresh the memory cache with a fresh Profiler read, then append
                // mem.* into the same list. Both stages omit their keys entirely when
                // unavailable (absent = not collected), so an inert release build with
                // no manual io feed and no mem support keeps metrics null. session_start
                // carries no metrics. The refreshed _memCache is also what position-
                // qualified Track() events attach (see Track()) until the next
                // heartbeat -- so this is the only place mem.* is ever sampled.
                List<FloatPair> metrics = null;
                if (eventName == HeartbeatEventName)
                {
                    _memCache.Refresh(_memSource);
                    metrics = _memCache.AppendTo(IoHeartbeat.BuildMetrics(_ioSource, _ioStats));
                }
                TrackInternal(
                    eventName,
                    mapId: "",
                    posX: 0f, posY: 0f, posZ: 0f,
                    source: TelemetrySource.Automated,
                    attributes: null,
                    metrics: metrics);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] TrackAutomated({eventName}) failed: {e}");
            }
        }

        private bool TrackInternal(
            string eventName,
            string mapId,
            float posX, float posY, float posZ,
            TelemetrySource source,
            List<StringPair> attributes,
            List<FloatPair> metrics,
            bool attachPerformance = true,
            bool preserveBufferedEvents = false)
        {
            // Run summaries must not enter legacy snapshot-based build aggregates.
            var perf = attachPerformance ? _perfCollector.Collect() : default;

            float? camYaw = null;
            float? camPitch = null;
            if (attachPerformance && _captureCameraRotation
                && CameraMath.TryUnpackCamera(
                    Interlocked.Read(ref _cameraSnapshot), out float unpackedYaw, out float unpackedPitch))
            {
                camYaw = unpackedYaw;
                camPitch = unpackedPitch;
            }

            // Resolve the CI session against this event from a SINGLE snapshot read, so the
            // stamped build_id and the merged ci.* attributes are always mutually consistent
            // even if Begin/EndAutomatedSession runs on the main thread while this Track()
            // executes on a background thread.
            var ciStamp = _session.ResolveSessionStamp(_buildId, attributes);

            var evt = new TelemetryEvent
            {
                EventName = eventName,
                TimestampUs = (DateTimeOffset.UtcNow.Ticks - 621355968000000000L) / 10L,
                SessionId = _session.SessionId,
                PlayerId = _session.PlayerId,
                PositionX = posX,
                PositionY = posY,
                PositionZ = posZ,
                MapId = mapId,
                Fps = perf.Fps,
                FrameTimeMs = perf.FrameTimeMs,
                MemoryUsedBytes = perf.MemoryUsedBytes,
                GpuTimeMs = perf.GpuTimeMs,
                Source = source,
                // The automated-session build_id override (CI) when active, else the
                // configured build_id -- resolved above. _buildId is never overwritten, so a
                // re-init or a direct build_id change can never strand a candidate id.
                BuildId = FieldClamp.Truncate(ciStamp.BuildId ?? "", FieldClamp.MaxBuildIdLength),
                Platform = _cachedPlatform,
                EngineVersion = _cachedEngineVersion,
                // The active automated-session attributes (CI metadata) merged with the
                // per-event ones -- from the same snapshot as BuildId -- so every event,
                // including the perf_heartbeat that feeds perf-diff, is tagged. No session
                // active -> the per-event list unchanged.
                Attributes = ciStamp.Attributes,
                Metrics = metrics,
                GameThreadMs = perf.GameThreadMs,
                RenderThreadMs = perf.RenderThreadMs,
                CameraYaw = camYaw,
                CameraPitch = camPitch,
            };

            bool preservePersistedPrefix = _offlineQueueActive
                && Volatile.Read(ref _pendingPersistedEventsToAck) > 0;
            bool preserveOldest = preservePersistedPrefix || preserveBufferedEvents;
            if (preserveOldest && !_buffer.TryEnqueuePreservingOldest(evt))
            {
                // The on-disk queue is positional, so overwriting its in-memory head would
                // make a later DropOldest acknowledge a different event. Let the main-thread
                // flush make room instead of doing disk I/O on the caller's Track path.
                _flushRequested = true;
                return false;
            }

            if (!preserveOldest)
            {
                _buffer.Enqueue(evt);
            }

            // Estimate payload size for flush threshold check.
            // Flag a flush when batch size or payload threshold is reached.
            // The actual flush is deferred to the main thread via FlushLoop
            // because StartCoroutine must be called from the main thread.
            var currentBytes = Interlocked.Add(
                ref _estimatedPayloadBytes, _flushPolicy.BytesPerEventEstimate);
            if (_flushPolicy.ShouldRequestFlush(_buffer.Count, currentBytes))
            {
                _flushRequested = true;
            }
            return true;
        }

        public void SetPlayerId(string playerId)
        {
            if (!_initialized)
            {
                Debug.LogWarning("[Framedash] SDK not initialized. Call Initialize() first.");
                return;
            }
            _session.SetPlayerId(playerId);
        }

        /// <summary>
        /// Manually report a disk I/O sample. Use this in RELEASE players (where the
        /// automatic AsyncReadManagerMetrics source is compiled out) or for custom
        /// loaders / a virtual file system that the engine metrics do not see. The
        /// sample accumulates into the current heartbeat window and is emitted, summed
        /// with any automatic-source data, as the io.read_bytes / io.read_time_ms /
        /// io.read_ops keys on the next perf_heartbeat. Thread-safe; negative or
        /// non-finite components are dropped. Never throws.
        /// </summary>
        /// <param name="bytes">Bytes read since the last report.</param>
        /// <param name="readTimeMs">Time spent reading, in milliseconds.</param>
        /// <param name="ops">Number of read operations completed.</param>
        public void ReportIoSample(long bytes, float readTimeMs, int ops)
        {
            try
            {
                // Gate on _initialized (not just a non-null accumulator): after
                // Shutdown() _ioStats stays non-null, and accumulating into a dead
                // session would waste memory/CPU until the next init.
                if (!_initialized) return;
                // Snapshot the field once: a concurrent re-init could swap it.
                var stats = _ioStats;
                if (stats == null) return;
                stats.Add(bytes, readTimeMs, ops);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] ReportIoSample() failed: {e}");
            }
        }

        // Monotonic wall-clock seconds from a high-resolution timer. Unaffected by
        // Time.timeScale or a paused game (unlike Time.time / Time.deltaTime), so a
        // load measured across a pause or slow-motion is still real elapsed time.
        private static double MonotonicSeconds()
            => (double)System.Diagnostics.Stopwatch.GetTimestamp()
                / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>
        /// Begin timing a map/level load. Records <paramref name="mapName"/> and a
        /// monotonic start timestamp; call <see cref="EndMapLoad"/> when loading
        /// completes to emit a <c>map_load</c> event whose <c>map_id</c> is deliberately
        /// EMPTY (keeping it out of the spatial heatmap grid and the activation gate); the
        /// map name rides <c>attributes["map_name"]</c> and the elapsed time rides the
        /// <c>metrics["load_time_ms"]</c> metric. The clock is
        /// wall-time monotonic (time-scale / pause safe). Calling BeginMapLoad again
        /// before EndMapLoad REPLACES the pending measurement (the earlier one is
        /// discarded). Main-thread only (pairs with <see cref="EndMapLoad"/>, whose event
        /// emission reads main-thread-only Unity APIs). Never throws. No-op if the SDK is
        /// not initialized.
        /// </summary>
        public void BeginMapLoad(string mapName)
        {
            try
            {
                if (!_initialized) return;
                _mapLoadTimer.Begin(mapName, MonotonicSeconds());
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] BeginMapLoad() failed: {e}");
            }
        }

        /// <summary>
        /// The Track path reads main-thread-only Unity Time and Profiler APIs. A worker-thread
        /// loader completion must return to the main thread or its event is dropped fail-safe.
        /// </summary>
        public void EndMapLoad()
        {
            try
            {
                if (!_initialized) return;
                if (!_mapLoadTimer.End(MonotonicSeconds(), out string mapName, out double elapsedMs))
                    return;
                TrackMapLoad(mapName, elapsedMs);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] EndMapLoad() failed: {e}");
            }
        }

        /// <summary>
        /// This uses the same main-thread-only Track path as EndMapLoad; worker-thread loaders
        /// must dispatch their report back to the main thread.
        /// </summary>
        public void ReportMapLoad(string mapName, double loadTimeMs)
        {
            try
            {
                if (!_initialized) return;
                if (!MapLoadTimer.IsValidLoadTimeMs(loadTimeMs)) return;
                TrackMapLoad(mapName, loadTimeMs);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] ReportMapLoad() failed: {e}");
            }
        }

        // Emit the map_load event through the public Track path so it inherits sampling,
        // field/attribute clamping (finite-metric drop, attribute-value truncation),
        // buffering, and CI session attributes -- it is a regular event, not a heartbeat.
        // map_id is left EMPTY (like perf_heartbeat) so this non-spatial event never lands
        // in the spatial heatmap grid query or the activation gate (both key on a non-empty
        // map_id); the loaded map name rides attributes["map_name"] instead, clamped to the
        // attribute-value cap by ClampAttributes. The load time rides metrics as
        // load_time_ms (no proto/CH field). A finite double that overflows float range
        // narrows to Infinity, which ClampMetrics would drop; skip it here so a map_load
        // without its load_time_ms metric is never emitted.
        private void TrackMapLoad(string mapName, double loadTimeMs)
        {
            float ms = (float)loadTimeMs;
            if (float.IsInfinity(ms)) return;
            var attributes = new Dictionary<string, string>(1) { { MapLoadTimer.KeyMapName, mapName ?? "" } };
            var metrics = new Dictionary<string, float>(1) { { MapLoadTimer.KeyLoadTimeMs, ms } };
            Track(MapLoadTimer.MapLoadEventName, mapId: "", attributes: attributes, metrics: metrics);
        }

        /// <summary>
        /// Begin an automated profiling session: tag every subsequent event with CI
        /// metadata so build-over-build performance can be compared in the dashboard and
        /// via <c>framedash perf-diff</c>. <paramref name="buildId"/> is stamped as the
        /// first-class build_id field; <paramref name="branch"/>, <paramref name="commit"/>
        /// and <paramref name="scenario"/> are attached as the <c>ci.branch</c> /
        /// <c>ci.commit</c> / <c>ci.scenario</c> attributes. Each call fully (re)defines the
        /// session rather than patching it: an omitted (null/empty) buildId clears any prior
        /// build_id override (events fall back to the configured build_id) and an omitted
        /// branch/commit/scenario is absent from the new tag set -- callers cannot
        /// incrementally update metadata across calls. With all arguments empty this is a
        /// no-op. Call once after Initialize(), before the profiling run. No-op if the SDK is
        /// not initialized.
        /// </summary>
        public void BeginAutomatedSession(string buildId = null, string branch = null,
            string commit = null, string scenario = null)
        {
            try
            {
                if (!_initialized)
                {
                    Debug.LogWarning("[Framedash] SDK not initialized. Call Initialize() before BeginAutomatedSession().");
                    return;
                }
                bool hasBuildId = !string.IsNullOrEmpty(buildId);
                bool hasBranch = !string.IsNullOrEmpty(branch);
                bool hasCommit = !string.IsNullOrEmpty(commit);
                bool hasScenario = !string.IsNullOrEmpty(scenario);
                // No metadata at all (e.g. BeginAutomatedSessionFromEnvironment with the
                // FRAMEDASH_* vars unset) is a true no-op: do not start an override or touch
                // session attributes, so a later End cannot clear state this call never set.
                if (!hasBuildId && !hasBranch && !hasCommit && !hasScenario) return;
                var attrs = new Dictionary<string, string>();
                if (hasBranch) attrs["ci.branch"] = branch;
                if (hasCommit) attrs["ci.commit"] = commit;
                if (hasScenario) attrs["ci.scenario"] = scenario;
                // Install the build_id override + ci.* attributes as one atomic snapshot. Each
                // Begin fully (re)defines the session: a supplied buildId becomes the override,
                // otherwise it is cleared back to the configured build_id fallback -- the same
                // replace-don't-merge semantics as the attributes, so no stale build_id leaks
                // from a prior session.
                _session.SetAutomatedSession(hasBuildId ? buildId : null, attrs);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] BeginAutomatedSession() failed: {e}");
            }
        }

        /// <summary>
        /// Begin an automated profiling session from the standard Framedash CI environment
        /// variables: <c>FRAMEDASH_BUILD_ID</c>, <c>FRAMEDASH_GIT_BRANCH</c>,
        /// <c>FRAMEDASH_GIT_COMMIT</c>, <c>FRAMEDASH_TEST_SCENARIO</c>. The
        /// <c>framedash run-profile-test</c> runner exports these before launching the
        /// game, so a CI integration needs only this one call in its automated-test entry
        /// point. With none of the variables set this is a no-op (no override is started).
        /// No-op if the SDK is not initialized.
        /// </summary>
        public void BeginAutomatedSessionFromEnvironment()
        {
            try
            {
                BeginAutomatedSession(
                    Environment.GetEnvironmentVariable("FRAMEDASH_BUILD_ID"),
                    Environment.GetEnvironmentVariable("FRAMEDASH_GIT_BRANCH"),
                    Environment.GetEnvironmentVariable("FRAMEDASH_GIT_COMMIT"),
                    Environment.GetEnvironmentVariable("FRAMEDASH_TEST_SCENARIO"));
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] BeginAutomatedSessionFromEnvironment() failed: {e}");
            }
        }

        /// <summary>
        /// End the automated profiling session: clear the <c>ci.*</c> session attributes set
        /// by <see cref="BeginAutomatedSession"/> AND drop the automated-session build_id
        /// override, so events emitted afterward carry the configured build_id again and are
        /// no longer folded into the candidate build's perf diff. Call <see cref="Flush"/>
        /// first if you want the buffered tagged events sent before the tags are cleared.
        /// No-op if the SDK is not initialized.
        /// </summary>
        public void EndAutomatedSession()
        {
            try
            {
                if (!_initialized) return;
                // One atomic clear: the build_id override and the ci.* attributes live in a
                // single session snapshot, so a background Track() either sees the whole
                // session or none of it -- a post-End event can never carry the candidate
                // build_id with cleared tags.
                _session.ClearSessionAttributes();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] EndAutomatedSession() failed: {e}");
            }
        }

        public void SetEventSamplingRate(string eventName, float rate)
        {
            if (!_initialized)
            {
                Debug.LogWarning("[Framedash] SDK not initialized. Call Initialize() first.");
                return;
            }
            _samplingPolicy.SetEventRate(FieldClamp.TruncateEventName(eventName), rate);
        }

        /// <summary>
        /// Remove a per-event-name sampling override so the event falls back to the global rate.
        /// Returns true if an override was present.
        /// </summary>
        public bool RemoveEventSamplingRate(string eventName)
        {
            if (!_initialized) return false;
            return _samplingPolicy.RemoveEventRate(FieldClamp.TruncateEventName(eventName));
        }

    }
}
