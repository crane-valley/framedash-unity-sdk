#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Framedash
{
    public sealed partial class TelemetrySDK : MonoBehaviour
    {
        private static TelemetrySDK? s_instance;
        private readonly object _lifecycleGate = new object();
        private const int DefaultMaxBatchSize = 100;
        // Matches the consumer's MAX_EVENTS_PER_BATCH (packages/ingest-core/src/config.ts):
        // a batch larger than the server cap is rejected wholesale, so allowing the
        // Inspector to configure one only loses data. 10,000 also equals the default
        // EventBuffer capacity; real flushes stay in the low hundreds (~100KB payload trigger).
        private const int MaxInspectorBatchSize = 10000;
        private const int MaxInspectorEventBufferCapacity = MaxInspectorBatchSize * 2;

        // Code constant, deliberately NOT serialized: a [SerializeField] version
        // would be captured into scenes/prefabs and deserialize the OLD value over
        // this initializer after a package upgrade, leaving X-SDK-Version stale.
        // Keep in sync with sdks/unity/package.json (release gotcha).
        private const string SdkVersion = "0.1.8";

        [Header("Configuration")]
        [SerializeField] private string _endpointUrl = "https://ingest.framedash.dev/v1/events";
        [SerializeField] private string _apiKey = "";
        [SerializeField] private string _buildId = "";
        [SerializeField] private string _playerId = "";
        [SerializeField] private bool _captureCameraRotation = true;

        [Header("Batching")]
        [SerializeField]
        [Range(1, MaxInspectorBatchSize)]
        private int _maxBatchSize = DefaultMaxBatchSize;
        [SerializeField]
        [Range(1, MaxInspectorEventBufferCapacity)]
        private int _eventBufferCapacity = EventBuffer.DefaultCapacity;
        [SerializeField] private float _flushIntervalSeconds = 30f;
        [SerializeField] private int _maxPayloadBytes = 102400;

        [Header("Sampling")]
        [SerializeField] [Range(0f, 1f)] private float _samplingRate = 1f;

        [Header("Diagnostics")]
        // Opt-in verbose transport logging (F29). When enabled, each batch send logs
        // the endpoint + compressed payload size and each successful flush logs
        // "Flushed N events (HTTP 202)". Off by default so a shipping game emits
        // nothing on the happy path; integrators flip it on to confirm delivery.
        [SerializeField] private bool _verboseLogging;

        [Header("Persistence")]
        // When enabled (default), events that cannot be sent (transient network failure
        // or app shutdown) are written to a small on-disk queue and retried next run.
        // Disable for a pure in-memory buffer with no disk writes.
        [SerializeField] private bool _enableOfflineQueue = true;

        // Unity constructs this component before InitializeInternal; _initialized gates these services.
        private EventBuffer _buffer = null!;
        private TransportLayer _transport = null!;
        private SessionManager _session = null!;
        private PerformanceCollector _perfCollector = null!;
        private SamplingPolicy _samplingPolicy = null!;
        private FlushPolicy _flushPolicy = null!;
        private Coroutine? _flushCoroutine;
        private bool _initialized;
        private int _estimatedPayloadBytes;
        private int _isFlushing;
        // Incremented on each (re)initialization; a FlushCoroutine only releases
        // _isFlushing if its captured generation still matches, so a stale flush from a
        // prior session cannot clear the guard for a new session's in-flight flush.
        private int _flushGeneration;
        private volatile bool _flushRequested;
        private bool _warnedEmptyPlayerId;
        private string _cachedPlatform = "";
        private string _cachedEngineVersion = "";
        // The automated-session (CI) build_id override and ci.* attributes live together in
        // the SessionManager as one immutable snapshot (see SessionManager.ResolveSessionStamp),
        // so the configured _buildId is never overwritten and the stamping path reads the
        // build_id and the tags from a single consistent point.
        private IPersistenceProvider _persistence = null!;
        // Captured from _enableOfflineQueue at init so a later inspector toggle cannot desync
        // the live provider mid-session.
        private bool _offlineQueueActive;
        private bool _persistenceAcknowledgementFailed;
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
        private Coroutine? _inFlightFlush;
        // The batch + persisted-prefix count that in-flight FlushCoroutine is sending,
        // captured alongside _inFlightFlush so FlushBlocking can reclaim and synchronously
        // deliver them: the blocked main thread cannot let that coroutine advance, so its
        // already-dequeued events would otherwise be stranded. Main-thread only.
        private TelemetryEvent[]? _inFlightBatch;
        private int _inFlightPersistedCount;
        // Captured on the main thread at Awake so FlushBlocking can reject an
        // off-main-thread call: it must never marshal-and-block, which would deadlock
        // against the very main loop the caller waits on. Volatile -- written on the main
        // thread, read from any thread.
        private volatile int _mainThreadId = -1;
        // The resolved (effective) API key the transport sends with -- the configured key,
        // or the FRAMEDASH_API_KEY fallback. Retained so the synchronous FlushBlocking path
        // uses the SAME credential as the async transport (which stores it privately). Never
        // promoted into _apiKey (see InitializeInternal).
        private string _effectiveApiKey = "";
        // Camera yaw/pitch sampled once per frame (Update) and stamped onto events,
        // mirroring the per-frame performance cache. Packed into one long and
        // published/read atomically so the (yaw, pitch) pair is always observed
        // coherently. CameraAbsent means "no camera this frame".
        private long _cameraSnapshot = CameraMath.CameraAbsent;
        private const float HeartbeatIntervalSeconds = 10f;
        private float _timeSinceLastHeartbeat;
        private IoStats _ioStats = null!;
        private IIoMetricsSource _ioSource = null!;
        // Memory readings (mem.vram, mem.heap): sampled fresh only on perf_heartbeat --
        // no windowing needed since Profiler exposes instantaneous totals, unlike the
        // cumulative io.* counters. Always non-null (the Profiler APIs are safe to
        // call in a release player); omission of a key happens inside MemoryMetricsCache.
        private readonly IMemoryMetricsSource _memSource = new UnityMemoryMetricsSource();
        // Caches the heartbeat's reading so position-qualified Track() events (a
        // non-empty map id) can also carry mem.* for the spatial heatmap grid, which
        // perf_heartbeat itself never reaches (empty map_id, no position). The
        // per-event path only reads this cache -- never Profiler directly.
        private readonly MemoryMetricsCache _memCache = new MemoryMetricsCache();
        private const string HeartbeatEventName = "perf_heartbeat";
        // Map/level load-time helper (BeginMapLoad/EndMapLoad/ReportMapLoad). Holds
        // the pending measurement and computes elapsed ms; the load time rides the
        // metrics map as load_time_ms on a "map_load" event (no proto/CH change,
        // mirroring the io.* attributes-map guardrail). Recreated on each (re-)init
        // so a new session never completes a load begun by a prior one.
        private MapLoadTimer _mapLoadTimer = null!;

        public string? SessionId
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

        public bool IsInitialized => _initialized;

        /// <summary>
        /// Settable from code so projects that initialize via `Initialize` can opt out without
        /// an inspector-attached component, e.g. `TelemetrySDK.Instance.CaptureCameraRotation =
        /// false;`. Takes effect from the next frame's capture.
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

        /// <summary>
        /// Opt-in verbose transport logging (F29, default false). When true, each batch
        /// send logs the endpoint + compressed payload size and each successful flush
        /// logs "Flushed N events (HTTP 202)" -- a client-side delivery confirmation for
        /// first-time integrators. Settable from code (e.g.
        /// <c>TelemetrySDK.Instance.VerboseLogging = true;</c>); a runtime change is
        /// propagated to the live transport, so it can be toggled after Initialize().
        /// Leave off in shipping builds to avoid per-flush log spam.
        /// </summary>
        public bool VerboseLogging
        {
            get => _verboseLogging;
            set
            {
                _verboseLogging = value;
                // Propagate a runtime toggle to the live transport so flipping this in
                // code (or the inspector) takes effect immediately instead of being
                // frozen at the value captured when the session was initialized. No-op
                // before init (applied when the transport is created).
                if (_transport != null) _transport.VerboseLogging = value;
            }
        }

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
        /// The Framedash ingest API key. Precedence: an explicit non-empty argument wins, else
        /// a key configured in the Inspector, else the `FRAMEDASH_API_KEY` environment variable
        /// (the CI path, consistent with the Framedash CLI). Passing null or empty keeps the
        /// Inspector-configured key (if any) rather than clearing it, so calling
        /// `Initialize(null, ...)` only to set other options does not override an Inspector key
        /// with the environment. Initialization fails with a logged error only when no source
        /// supplies a key.
        ///
        /// When true (default), unsent events are persisted to disk and retried next run. Pass
        /// false for a pure in-memory buffer with no disk writes -- the only way a code-only
        /// integration (which auto-creates the component) can opt out, since the inspector
        /// field is never seen.
        /// </summary>
        public static TelemetrySDK Initialize(string? apiKey = null, string? endpointUrl = null, string? buildId = null, string? playerId = null, bool enableOfflineQueue = true)
        {
            var sdk = Instance;
            lock (sdk._lifecycleGate)
            {
                // Only overwrite the configured key when an explicit non-empty argument is
                // given. A null/empty apiKey means "keep whatever is configured" (e.g. an
                // Inspector key) so a caller passing Initialize(null, ...) purely to set other
                // options does not wipe the Inspector key and hand precedence to the env var.
                // Precedence stays: explicit non-empty arg > Inspector field > FRAMEDASH_API_KEY.
                if (!string.IsNullOrEmpty(apiKey)) sdk._apiKey = apiKey;
                if (!string.IsNullOrEmpty(endpointUrl)) sdk._endpointUrl = endpointUrl;
                if (!string.IsNullOrEmpty(buildId)) sdk._buildId = buildId;
                if (playerId != null) sdk._playerId = playerId;
                sdk._enableOfflineQueue = enableOfflineQueue;
                sdk.InitializeInternal();
            }
            return sdk;
        }

        private void Awake()
        {
            // Awake always runs on Unity's main thread; record it so FlushBlocking can
            // reject an off-main-thread call rather than marshaling-and-blocking.
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
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
            // Auto-init only when a key is set in the Inspector. The FRAMEDASH_API_KEY
            // env fallback (F32) is intentionally NOT consulted here: auto-initializing
            // from the environment would race a later explicit Initialize("key") (Unity's
            // Start() order is undefined), letting env win over an explicit key and
            // violating the "explicit wins" precedence. Env-only (CI) integrations call
            // Initialize() -- which applies the fallback in InitializeInternal -- exactly
            // like the CLI consults FRAMEDASH_API_KEY when a command is invoked.
            if (!_initialized && !string.IsNullOrEmpty(_apiKey))
            {
                InitializeInternal();
            }
        }

        private string ResolveApiKey()
            => ApiKeyResolver.Resolve(_apiKey, () => Environment.GetEnvironmentVariable("FRAMEDASH_API_KEY"));

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
                UpdatePerformanceRun();
                _perfCollector.UpdateFrameTimings();
                if (_captureCameraRotation) UpdateCameraRotation();
                _timeSinceLastHeartbeat += Time.unscaledDeltaTime;
                if (_timeSinceLastHeartbeat >= HeartbeatIntervalSeconds)
                {
                    _timeSinceLastHeartbeat = 0f;
                    TrackAutomated(HeartbeatEventName);
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
            lock (_lifecycleGate)
            {
                if (_initialized) return;
                // Resolve the effective key FRESH on every initialization (F32): the
                // configured key (Inspector/explicit arg) wins, otherwise the CURRENT
                // FRAMEDASH_API_KEY. Resolve into a LOCAL and never promote it into _apiKey --
                // promoting the env value would freeze the first read, so a changed
                // FRAMEDASH_API_KEY after Shutdown() + Initialize() in the same process would
                // never be picked up and the config-vs-env precedence would blur. _apiKey
                // stays the configured key only; effectiveApiKey feeds the transport and the
                // offline-queue partition key below.
                string effectiveApiKey = ResolveApiKey();
                if (string.IsNullOrEmpty(effectiveApiKey))
                {
                    Debug.LogError("[Framedash] API key is required. Set it via TelemetrySDK.Initialize(apiKey), the Inspector, or the FRAMEDASH_API_KEY environment variable.");
                    return;
                }

                if (!Uri.TryCreate(_endpointUrl, UriKind.Absolute, out var parsedUri) ||
                    (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
                {
                    Debug.LogError("[Framedash] endpointUrl must be a valid HTTP(S) URL.");
                    return;
                }

                int maxBatchSize = ResolveMaxBatchSize();
                // Retained envelopes and active sends belong to the previous endpoint/key.
                if (_inFlightFlush != null)
                {
                    StopCoroutine(_inFlightFlush);
                    _transport?.AbortInFlightRequest();
                    _inFlightFlush = null;
                }
                _inFlightBatch = null;
                _inFlightPersistedCount = 0;
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
                    ? (IPersistenceProvider)new FilePersistence(FilePersistence.DefaultQueueFilePath(_endpointUrl + "\n" + effectiveApiKey))
                    : new NullPersistence();
                _pendingPersistedEventsToAck = 0;
                _persistenceAcknowledgementFailed = false;
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

                // Retain the effective key so the synchronous FlushBlocking path sends with
                // the same credential as the async transport.
                _effectiveApiKey = effectiveApiKey;
                _transport = new TransportLayer(_endpointUrl, effectiveApiKey, SdkVersion, _maxPayloadBytes, _verboseLogging);
                _session = new SessionManager(_playerId);
                _perfCollector = new PerformanceCollector();
                _perfCollector.UpdateFrameTimings();
                // Reset the camera snapshot so a re-init (Shutdown then Initialize) does
                // not stamp session_start / pre-first-Update events with a stale reading.
                Interlocked.Exchange(ref _cameraSnapshot, CameraMath.CameraAbsent);
                _samplingPolicy = new SamplingPolicy(_samplingRate);
                _flushPolicy = new FlushPolicy(maxBatchSize, _maxPayloadBytes, _flushIntervalSeconds);
                _cachedPlatform = FieldClamp.Truncate(Application.platform.ToString(), FieldClamp.MaxPlatformLength);
                _cachedEngineVersion = FieldClamp.Truncate(Application.unityVersion, FieldClamp.MaxEngineVersionLength);

                _timeSinceLastHeartbeat = 0f;
                _ioStats = new IoStats();
                _ioSource = AsyncReadManagerIoSource.TryCreate();
                // Eager first sample at init (rather than a lazy sample on the first
                // qualifying event) so a position-qualified Track() call in the first
                // ~10s of a session -- before the first heartbeat -- is not blind. This
                // is a one-time init cost, not a per-event one, so it does not conflict
                // with the "no Profiler calls on the per-event path" rule below.
                _memCache.Refresh(_memSource);
                _mapLoadTimer = new MapLoadTimer();
                _flushCoroutine = StartCoroutine(FlushLoop());
                _initialized = true;

                Debug.Log($"[Framedash] SDK initialized. Session: {_session.SessionId}");
                TrackAutomated("session_start");
            }
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

    }
}
