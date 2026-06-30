using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Framedash
{
    /// <summary>
    /// Main entry point for the Framedash Telemetry SDK.
    /// Attach to a persistent GameObject or use <see cref="Initialize"/>.
    /// </summary>
    public sealed class TelemetrySDK : MonoBehaviour
    {
        private static TelemetrySDK s_instance;
        private const int DefaultMaxBatchSize = 100;
        // Matches the consumer's MAX_EVENTS_PER_BATCH (packages/ingest-core/src/proto-decode.ts):
        // a batch larger than the server cap is rejected wholesale, so allowing the
        // Inspector to configure one only loses data. 10,000 also equals the default
        // EventBuffer capacity; real flushes stay in the low hundreds (~100KB payload trigger).
        private const int MaxInspectorBatchSize = 10000;
        private const int MaxInspectorEventBufferCapacity = MaxInspectorBatchSize * 2;
        // event_name truncation is centralized in FieldClamp.TruncateEventName
        // (surrogate-pair safe); see FieldClamp.MaxEventNameLength.

        [Header("Configuration")]
        [SerializeField] private string _endpointUrl = "https://ingest.framedash.dev/v1/events";
        [SerializeField] private string _apiKey;
        [SerializeField] private string _buildId;
        [SerializeField] private string _sdkVersion = "0.1.1";
        [SerializeField] private string _playerId;
        [SerializeField] private bool _captureCameraRotation = true;

        [Header("Batching")]
        [SerializeField]
        [Range(1, MaxInspectorBatchSize)]
        private int _maxBatchSize = DefaultMaxBatchSize;
        [SerializeField]
        [Range(1, MaxInspectorEventBufferCapacity)]
        private int _eventBufferCapacity = EventBuffer.DefaultCapacity;
        [SerializeField] private float _flushIntervalSeconds = 30f;
        [SerializeField] private int _maxPayloadBytes = 102400; // 100KB

        [Header("Sampling")]
        [SerializeField] [Range(0f, 1f)] private float _samplingRate = 1f;

        [Header("Persistence")]
        // When enabled (default), events that cannot be sent (transient network failure
        // or app shutdown) are written to a small on-disk queue and retried next run.
        // Disable for a pure in-memory buffer with no disk writes.
        [SerializeField] private bool _enableOfflineQueue = true;

        private EventBuffer _buffer;
        private TransportLayer _transport;
        private SessionManager _session;
        private PerformanceCollector _perfCollector;
        private SamplingPolicy _samplingPolicy;
        private FlushPolicy _flushPolicy;
        private Coroutine _flushCoroutine;
        private bool _initialized;
        private int _estimatedPayloadBytes;
        private int _isFlushing; // 0 = idle, 1 = flushing (atomic via Interlocked)
        // Incremented on each (re)initialization; a FlushCoroutine only releases
        // _isFlushing if its captured generation still matches, so a stale flush from a
        // prior session cannot clear the guard for a new session's in-flight flush.
        private int _flushGeneration;
        private volatile bool _flushRequested;
        private bool _warnedEmptyPlayerId;
        private string _cachedPlatform;
        private string _cachedEngineVersion;
        // The automated-session (CI) build_id override and ci.* attributes live together in
        // the SessionManager as one immutable snapshot (see SessionManager.ResolveSessionStamp),
        // so the configured _buildId is never overwritten and the stamping path reads the
        // build_id and the tags from a single consistent point.
        private IPersistenceProvider _persistence;
        // True when the offline queue is active (a FilePersistence is in use). Captured
        // from _enableOfflineQueue at init so a later inspector toggle cannot desync the
        // live provider mid-session.
        private bool _offlineQueueActive;
        // Number of leading buffered events already on disk (restored from a prior run,
        // or a previous flush's persisted block). The first N events the buffer dequeues
        // map, in order, to the first N events of the on-disk queue, so a successful
        // flush acks them with DropOldest and a failed flush leaves them on disk (never
        // double-persisted). Main-thread only (init + flush), like _flushGeneration.
        private int _pendingPersistedEventsToAck;
        // Snapshot of _buffer.DroppedCount taken after restore. If the ring later drops
        // events (a burst exceeding the capacity floor) while persisted events are still
        // pending ack, the in-memory head no longer matches the on-disk head, so the queue
        // is conservatively cleared rather than risk acking the wrong events.
        private int _persistedDropBaseline;
        // The batch an in-flight FlushCoroutine is sending, retained so Shutdown can stop
        // that coroutine and let its (generation-gated) finally persist the undelivered
        // events instead of losing them on quit.
        private Coroutine _inFlightFlush;
        // Camera yaw/pitch sampled once per frame (Update) and stamped onto events,
        // mirroring the per-frame performance cache. Packed into one long and
        // published/read atomically so the (yaw, pitch) pair is always observed
        // coherently. CameraAbsent means "no camera this frame".
        private long _cameraSnapshot = CameraMath.CameraAbsent;
        private const float HeartbeatIntervalSeconds = 10f;
        private float _timeSinceLastHeartbeat;

        /// <summary>Current session ID, or null if SDK is not initialized.</summary>
        public string SessionId
        {
            get
            {
                if (!_initialized || _session == null)
                {
                    Debug.LogWarning("[Framedash] SDK is not initialized. Call Initialize() first.");
                    return null;
                }
                return _session.SessionId;
            }
        }

        /// <summary>Whether the SDK is initialized and ready to track events.</summary>
        public bool IsInitialized => _initialized;

        /// <summary>
        /// Whether the SDK records the main camera's yaw/pitch on each event
        /// (default true). Settable from code so projects that initialize via
        /// <see cref="Initialize"/> can opt out without an inspector-attached
        /// component, e.g. <c>TelemetrySDK.Instance.CaptureCameraRotation = false;</c>.
        /// Takes effect from the next frame's capture.
        /// </summary>
        public bool CaptureCameraRotation
        {
            get => _captureCameraRotation;
            set
            {
                _captureCameraRotation = value;
                // Drop any cached sample so a toggle never stamps a stale reading;
                // the next Update() repopulates it while enabled.
                Interlocked.Exchange(ref _cameraSnapshot, CameraMath.CameraAbsent);
            }
        }

        /// <summary>Singleton instance. Created automatically if needed.</summary>
        public static TelemetrySDK Instance
        {
            get
            {
                if (s_instance == null)
                {
#if UNITY_2023_1_OR_NEWER
                    s_instance = FindAnyObjectByType<TelemetrySDK>();
#else
                    s_instance = FindObjectOfType<TelemetrySDK>();
#endif
                }
                if (s_instance == null)
                {
                    var go = new GameObject("[Framedash]");
                    s_instance = go.AddComponent<TelemetrySDK>();
                    DontDestroyOnLoad(go);
                }
                return s_instance;
            }
        }

        /// <summary>
        /// Initialize the SDK with the given configuration.
        /// Call this once at game startup (e.g., in a boot scene).
        /// </summary>
        /// <param name="enableOfflineQueue">
        /// When true (default), unsent events are persisted to disk and retried next run.
        /// Pass false for a pure in-memory buffer with no disk writes -- the only way a
        /// code-only integration (which auto-creates the component) can opt out, since the
        /// inspector field is never seen.
        /// </param>
        public static TelemetrySDK Initialize(string apiKey, string endpointUrl = null, string buildId = null, string playerId = null, bool enableOfflineQueue = true)
        {
            var sdk = Instance;
            sdk._apiKey = apiKey;
            if (!string.IsNullOrEmpty(endpointUrl)) sdk._endpointUrl = endpointUrl;
            if (!string.IsNullOrEmpty(buildId)) sdk._buildId = buildId;
            if (playerId != null) sdk._playerId = playerId;
            sdk._enableOfflineQueue = enableOfflineQueue;
            sdk.InitializeInternal();
            return sdk;
        }

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }
            s_instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (!_initialized && !string.IsNullOrEmpty(_apiKey))
            {
                InitializeInternal();
            }
        }

        // Set after the first Update() / camera-sampling exception so a persistent
        // per-frame failure logs once (with the full exception) instead of flooding
        // every frame; the work keeps running so a transient hiccup still recovers.
        private bool _loggedUpdateError;
        private bool _loggedCameraError;

        private void Update()
        {
            if (!_initialized) return;

            // Wrap the per-frame body so a telemetry hiccup can never throw out of
            // Unity's Update callback and disrupt the game (the NEVER-throw hard rule),
            // mirroring Godot's _Process-wide try/catch.
            try
            {
                _perfCollector.UpdateFrameTimings();
                if (_captureCameraRotation) UpdateCameraRotation();
                _timeSinceLastHeartbeat += Time.unscaledDeltaTime;
                if (_timeSinceLastHeartbeat >= HeartbeatIntervalSeconds)
                {
                    _timeSinceLastHeartbeat = 0f;
                    TrackAutomated("perf_heartbeat");
                }
            }
            catch (Exception e)
            {
                // Log once with the full exception (type + stack); suppress the rest
                // so a persistent per-frame failure cannot flood the log.
                if (!_loggedUpdateError)
                {
                    _loggedUpdateError = true;
                    Debug.LogError($"[Framedash] Update() failed (subsequent occurrences suppressed): {e}");
                }
            }
        }

        // Sample the main camera once per frame (Update) so each event is stamped
        // with the latest orientation without re-reading Unity APIs per event.
        private void UpdateCameraRotation()
        {
            // Wrap the body so a Unity API hiccup (e.g. a destroyed transform) cannot
            // throw out of the per-frame camera sample, mirroring Godot's _Process-wide
            // try/catch. On failure, drop to the absent sentinel.
            try
            {
                // Sample Camera.main every frame so a switched/retagged MainCamera is
                // always reflected (a cached reference would pin a stale camera until
                // it was destroyed). Camera.main is null on headless/dedicated builds.
                var cam = Camera.main;
                if (cam == null)
                {
                    Interlocked.Exchange(ref _cameraSnapshot, CameraMath.CameraAbsent);
                    return;
                }

                Vector3 euler = cam.transform.eulerAngles;
                float yaw = CameraMath.NormalizeYaw(euler.y);
                float pitch = CameraMath.PitchFromEulerX(euler.x);

                // Finite-only: publish the coherent pair, or the absent sentinel.
                if (float.IsNaN(yaw) || float.IsInfinity(yaw) ||
                    float.IsNaN(pitch) || float.IsInfinity(pitch))
                {
                    Interlocked.Exchange(ref _cameraSnapshot, CameraMath.CameraAbsent);
                    return;
                }

                Interlocked.Exchange(ref _cameraSnapshot, CameraMath.PackCamera(yaw, pitch));
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _cameraSnapshot, CameraMath.CameraAbsent);
                if (!_loggedCameraError)
                {
                    _loggedCameraError = true;
                    Debug.LogWarning($"[Framedash] UpdateCameraRotation() failed (subsequent occurrences suppressed): {e}");
                }
            }
        }

        private void InitializeInternal()
        {
            if (_initialized) return;
            if (string.IsNullOrEmpty(_apiKey))
            {
                Debug.LogError("[Framedash] API key is required. Call TelemetrySDK.Initialize(apiKey).");
                return;
            }

            // Validate endpoint URL
            if (!Uri.TryCreate(_endpointUrl, UriKind.Absolute, out var parsedUri) ||
                (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            {
                Debug.LogError("[Framedash] endpointUrl must be a valid HTTP(S) URL.");
                return;
            }

            int maxBatchSize = ResolveMaxBatchSize();
            // Re-init starts fresh: clear flush state left over from a prior session
            // (Shutdown then Initialize) so a previous in-flight flush cannot block the
            // new session's flushes -- including session_start -- via the single-flight
            // guard. Bump the generation so a stale FlushCoroutine completion from a
            // prior session will not release this session's flush guard (see FlushCoroutine).
            _flushRequested = false;
            Interlocked.Exchange(ref _isFlushing, 0);
            Interlocked.Exchange(ref _estimatedPayloadBytes, 0);
            _flushGeneration++;
            _offlineQueueActive = _enableOfflineQueue;
            // A fresh session owns no automated-session state: the SessionManager (which holds
            // the build_id override + ci.* snapshot) is recreated below, so a prior Begin
            // without an End cannot keep stamping events under the candidate build.

            int bufferCapacity = ResolveEventBufferCapacity(maxBatchSize);
            // With the offline queue on, the buffer must hold the entire restored queue
            // plus a batch without the ring dropping a restored event -- otherwise the
            // in-memory head would stop matching the on-disk head and an ack (DropOldest)
            // could remove the wrong persisted events. Floor the capacity at
            // MaxPersistedEvents + maxBatchSize (matching the UE5 EventBufferCapacity).
            if (_offlineQueueActive)
            {
                int offlineFloor = FilePersistence.MaxPersistedEvents + maxBatchSize;
                if (bufferCapacity < offlineFloor) bufferCapacity = offlineFloor;
            }
            _buffer = new EventBuffer(bufferCapacity);

            // Offline queue: pick the provider and restore any events a prior run (or a
            // Shutdown) persisted. Restored events are enqueued into the fresh buffer
            // first, so they sit at the head and flush before new events.
            // _pendingPersistedEventsToAck records how many leading buffer events are
            // already on disk (capped at the buffer's count, a belt-and-braces guard on
            // top of the capacity floor above).
            // Partition the on-disk queue by ingest config (endpoint + API key) so moving
            // the same install between local/staging/prod, or rotating keys, never resends
            // one project's events to another.
            _persistence = _offlineQueueActive
                ? (IPersistenceProvider)new FilePersistence(FilePersistence.DefaultQueueFilePath(_endpointUrl + "\n" + _apiKey))
                : new NullPersistence();
            _pendingPersistedEventsToAck = 0;
            if (_offlineQueueActive)
            {
                TelemetryEvent[] restored = _persistence.Load();
                if (restored.Length > 0)
                {
                    foreach (var restoredEvent in restored) _buffer.Enqueue(restoredEvent);
                    _pendingPersistedEventsToAck = Math.Min(restored.Length, _buffer.Count);
                    Debug.Log($"[Framedash] Restored {restored.Length} persisted event(s) to the offline queue.");
                }
            }
            // Baseline for the head-alignment guard (see Flush). The capacity floor above
            // means restore itself never drops, so this captures a clean starting point.
            _persistedDropBaseline = _buffer.DroppedCount;

            _transport = new TransportLayer(_endpointUrl, _apiKey, _sdkVersion, _maxPayloadBytes);
            _session = new SessionManager(_playerId);
            _perfCollector = new PerformanceCollector();
            // Reset the camera snapshot so a re-init (Shutdown then Initialize) does
            // not stamp session_start / pre-first-Update events with a stale reading.
            Interlocked.Exchange(ref _cameraSnapshot, CameraMath.CameraAbsent);
            _samplingPolicy = new SamplingPolicy(_samplingRate);
            _flushPolicy = new FlushPolicy(maxBatchSize, _maxPayloadBytes, _flushIntervalSeconds);
            // Truncate to the ingest caps for parity with the other string fields
            // (these are always short in practice, but clamp defensively).
            _cachedPlatform = FieldClamp.Truncate(Application.platform.ToString(), FieldClamp.MaxPlatformLength);
            _cachedEngineVersion = FieldClamp.Truncate(Application.unityVersion, FieldClamp.MaxEngineVersionLength);

            _timeSinceLastHeartbeat = 0f;
            _flushCoroutine = StartCoroutine(FlushLoop());
            _initialized = true;

            Debug.Log($"[Framedash] SDK initialized. Session: {_session.SessionId}");
            TrackAutomated("session_start");
        }

        private int ResolveMaxBatchSize()
        {
            int maxBatchSize = _maxBatchSize;
            if (maxBatchSize <= 0)
            {
                maxBatchSize = DefaultMaxBatchSize;
                Debug.LogWarning($"[Framedash] Max batch size must be > 0. Using default {maxBatchSize}.");
            }

            if (maxBatchSize > MaxInspectorBatchSize)
            {
                Debug.LogWarning(
                    $"[Framedash] Max batch size ({maxBatchSize}) exceeds the supported maximum " +
                    $"({MaxInspectorBatchSize}). Clamping to supported maximum.");
                maxBatchSize = MaxInspectorBatchSize;
            }

            return maxBatchSize;
        }

        private int ResolveEventBufferCapacity(int maxBatchSize)
        {
            int capacity = _eventBufferCapacity;
            if (capacity <= 0)
            {
                capacity = EventBuffer.DefaultCapacity;
                Debug.LogWarning($"[Framedash] Event buffer capacity must be > 0. Using default {capacity}.");
            }

            if (capacity > MaxInspectorEventBufferCapacity)
            {
                Debug.LogWarning(
                    $"[Framedash] Event buffer capacity ({capacity}) exceeds the supported maximum " +
                    $"({MaxInspectorEventBufferCapacity}). Clamping to supported maximum.");
                capacity = MaxInspectorEventBufferCapacity;
            }

            int safetyMargin = maxBatchSize * 2;

            if (capacity < safetyMargin)
            {
                Debug.LogWarning(
                    $"[Framedash] Event buffer capacity ({capacity}) is smaller than recommended safety margin " +
                    $"({safetyMargin}). Clamping to safety margin.");
                capacity = safetyMargin;
            }

            return capacity;
        }

        /// <summary>
        /// Track a custom event.
        /// </summary>
        /// <param name="eventName">Name of the event (e.g. "player_death", "zone_enter"). Must not be null or empty.</param>
        /// <param name="mapId">Optional map identifier for spatial context.</param>
        /// <param name="position">Optional world-space position where the event occurred.</param>
        /// <param name="attributes">Optional string key-value pairs for categorical data.</param>
        /// <param name="metrics">Optional float key-value pairs for numerical measurements.</param>
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

                TrackInternal(
                    safeEventName,
                    FieldClamp.Truncate(mapId ?? "", FieldClamp.MaxMapIdLength),
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
                TrackInternal(
                    eventName,
                    mapId: "",
                    posX: 0f, posY: 0f, posZ: 0f,
                    source: TelemetrySource.Automated,
                    attributes: null,
                    metrics: null);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] TrackAutomated({eventName}) failed: {e}");
            }
        }

        // Shared event-construction and enqueue/flush-check logic.
        // All caller-specific gates (initialization check, name validation,
        // sampling, player-ID warning, attribute conversion) run in the caller
        // before this method is invoked with fully resolved values.
        private void TrackInternal(
            string eventName,
            string mapId,
            float posX, float posY, float posZ,
            TelemetrySource source,
            List<StringPair> attributes,
            List<FloatPair> metrics)
        {
            var perf = _perfCollector.Collect();

            // Read the camera snapshot atomically; TryUnpackCamera yields a coherent
            // pair or nothing (both-or-neither). The serializer is the final guard.
            float? camYaw = null;
            float? camPitch = null;
            if (_captureCameraRotation
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
                // Unix epoch in .NET ticks (621355968000000000L), divided by 10
                // to convert 100ns ticks to microseconds for true microsecond precision.
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

            _buffer.Enqueue(evt);

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
        }

        /// <summary>
        /// Set the player ID at runtime (e.g. after login).
        /// Pass null or empty to revert to anonymous.
        /// </summary>
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
        /// <c>FRAMEDASH_GIT_COMMIT</c>, <c>FRAMEDASH_TEST_SCENARIO</c>. The planned
        /// <c>framedash run-profile-test</c> runner will export these before launching the
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

        /// <summary>
        /// Set a per-event-name sampling rate that overrides the global rate for that event.
        /// Empty event names are ignored. Rate is clamped to [0, 1].
        /// Has no effect if the SDK is not initialized.
        /// </summary>
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

        /// <summary>Flush all buffered events immediately. Must be called from the main thread.</summary>
        public void Flush()
        {
            try
            {
                if (Interlocked.CompareExchange(ref _isFlushing, 1, 0) != 0) return;
                if (!_initialized || _buffer.Count == 0)
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

                TelemetryEvent[] batch = _buffer.DequeueAll();
                // The leading min(pendingAck, batch) events are already on disk; mark
                // them so the flush can ack (DropOldest) them on success and avoid
                // re-persisting them on failure. They leave the buffer now, so drop them
                // from the pending count (whether the send succeeds or not).
                int persistedCount = Math.Min(_pendingPersistedEventsToAck, batch.Length);
                _pendingPersistedEventsToAck -= persistedCount;
                _inFlightFlush = StartCoroutine(FlushCoroutine(batch, _flushGeneration, persistedCount));
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _isFlushing, 0);
                Debug.LogError($"[Framedash] Flush() failed: {e}");
            }
        }

        private IEnumerator FlushCoroutine(TelemetryEvent[] events, int generation, int persistedCount)
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
                    ApplyPersistenceResult(events, persistedCount, result.DeliveredLeadingCount);
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

        /// <summary>Shutdown the SDK gracefully.</summary>
        public void Shutdown()
        {
            try
            {
                if (!_initialized) return;
                if (_flushCoroutine != null) StopCoroutine(_flushCoroutine);
                // Stop the in-flight send (if any) so its generation-gated finally runs now
                // and persists the batch it had already dequeued -- otherwise those events,
                // which are no longer in _buffer, would be lost on quit. Stopping a finished
                // coroutine is a no-op. The finally sees DeliveredLeadingCount unset (0) for
                // an interrupted send, so it persists the whole undelivered tail.
                if (_inFlightFlush != null)
                {
                    StopCoroutine(_inFlightFlush);
                    _inFlightFlush = null;
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
                    // No disk fallback -- best-effort final network flush (may not finish on quit).
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
