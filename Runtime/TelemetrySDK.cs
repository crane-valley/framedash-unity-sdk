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
        // Must stay in sync with packages/ingest-core/src/config.ts MAX_EVENT_NAME_LEN.
        private const int MaxEventNameLength = 128;

        private static string TruncateEventName(string eventName)
        {
            if (string.IsNullOrEmpty(eventName) || eventName.Length <= MaxEventNameLength) return eventName;
            return eventName.Substring(0, MaxEventNameLength);
        }

        [Header("Configuration")]
        [SerializeField] private string _endpointUrl = "https://ingest.framedash.dev/v1/events";
        [SerializeField] private string _apiKey;
        [SerializeField] private string _buildId;
        [SerializeField] private string _sdkVersion = "0.1.0";
        [SerializeField] private string _playerId;

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
        private volatile bool _flushRequested;
        private bool _warnedEmptyPlayerId;
        private string _cachedPlatform;
        private string _cachedEngineVersion;
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
        public static TelemetrySDK Initialize(string apiKey, string endpointUrl = null, string buildId = null, string playerId = null)
        {
            var sdk = Instance;
            sdk._apiKey = apiKey;
            if (!string.IsNullOrEmpty(endpointUrl)) sdk._endpointUrl = endpointUrl;
            if (!string.IsNullOrEmpty(buildId)) sdk._buildId = buildId;
            if (playerId != null) sdk._playerId = playerId;
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

        private void Update()
        {
            if (!_initialized) return;
            _perfCollector.UpdateFrameTimings();
            _timeSinceLastHeartbeat += Time.unscaledDeltaTime;
            if (_timeSinceLastHeartbeat >= HeartbeatIntervalSeconds)
            {
                _timeSinceLastHeartbeat = 0f;
                TrackAutomated("perf_heartbeat");
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
            _buffer = new EventBuffer(ResolveEventBufferCapacity(maxBatchSize));
            _transport = new TransportLayer(_endpointUrl, _apiKey, _sdkVersion, _maxPayloadBytes);
            _session = new SessionManager(_playerId);
            _perfCollector = new PerformanceCollector();
            _samplingPolicy = new SamplingPolicy(_samplingRate);
            _flushPolicy = new FlushPolicy(maxBatchSize, _maxPayloadBytes, _flushIntervalSeconds);
            _cachedPlatform = Application.platform.ToString();
            _cachedEngineVersion = Application.unityVersion;

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

                if (string.IsNullOrEmpty(eventName))
                {
                    Debug.LogWarning("[Framedash] eventName must not be null or empty. Event dropped.");
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
                string safeEventName = TruncateEventName(eventName);

                // Sampling check — skip expensive perf collection if event is dropped
                if (!_samplingPolicy.ShouldSample(safeEventName))
                    return;

                var perf = _perfCollector.Collect();

                // Convert Dictionary parameters to serializable List types
                List<StringPair> attrList = null;
                if (attributes != null)
                {
                    attrList = new List<StringPair>(attributes.Count);
                    foreach (var kvp in attributes)
                        attrList.Add(new StringPair(kvp.Key, kvp.Value));
                }

                List<FloatPair> metricList = null;
                if (metrics != null)
                {
                    metricList = new List<FloatPair>(metrics.Count);
                    foreach (var kvp in metrics)
                        metricList.Add(new FloatPair(kvp.Key, kvp.Value));
                }

                var evt = new TelemetryEvent
                {
                    EventName = safeEventName,
                    // Unix epoch in .NET ticks (621355968000000000L), divided by 10
                    // to convert 100ns ticks to microseconds for real μs precision.
                    TimestampUs = (DateTimeOffset.UtcNow.Ticks - 621355968000000000L) / 10L,
                    SessionId = _session.SessionId,
                    PlayerId = _session.PlayerId,
                    PositionX = position?.x ?? 0f,
                    PositionY = position?.y ?? 0f,
                    PositionZ = position?.z ?? 0f,
                    MapId = mapId,
                    Fps = perf.Fps,
                    FrameTimeMs = perf.FrameTimeMs,
                    MemoryUsedBytes = perf.MemoryUsedBytes,
                    GpuTimeMs = perf.GpuTimeMs,
                    Source = TelemetrySource.Player,
                    BuildId = _buildId ?? "",
                    Platform = _cachedPlatform,
                    EngineVersion = _cachedEngineVersion,
                    Attributes = attrList,
                    Metrics = metricList,
                    GameThreadMs = perf.GameThreadMs,
                    RenderThreadMs = perf.RenderThreadMs,
                };

                _buffer.Enqueue(evt);

                // Estimate payload size for flush threshold check
                var currentBytes = Interlocked.Add(
                    ref _estimatedPayloadBytes, _flushPolicy.BytesPerEventEstimate);

                // Flag a flush when batch size or payload threshold is reached.
                // The actual flush is deferred to the main thread via FlushLoop
                // because StartCoroutine must be called from the main thread.
                if (_flushPolicy.ShouldRequestFlush(_buffer.Count, currentBytes))
                {
                    _flushRequested = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] Track() failed: {e.Message}");
            }
        }

        private void TrackAutomated(string eventName)
        {
            try
            {
                var perf = _perfCollector.Collect();

                var evt = new TelemetryEvent
                {
                    EventName = eventName,
                    TimestampUs = (DateTimeOffset.UtcNow.Ticks - 621355968000000000L) / 10L,
                    SessionId = _session.SessionId,
                    PlayerId = _session.PlayerId,
                    PositionX = 0f,
                    PositionY = 0f,
                    PositionZ = 0f,
                    MapId = "",
                    Fps = perf.Fps,
                    FrameTimeMs = perf.FrameTimeMs,
                    MemoryUsedBytes = perf.MemoryUsedBytes,
                    GpuTimeMs = perf.GpuTimeMs,
                    Source = TelemetrySource.Automated,
                    BuildId = _buildId ?? "",
                    Platform = _cachedPlatform,
                    EngineVersion = _cachedEngineVersion,
                    Attributes = null,
                    Metrics = null,
                    GameThreadMs = perf.GameThreadMs,
                    RenderThreadMs = perf.RenderThreadMs,
                };

                _buffer.Enqueue(evt);

                var currentBytes = Interlocked.Add(
                    ref _estimatedPayloadBytes, _flushPolicy.BytesPerEventEstimate);
                if (_flushPolicy.ShouldRequestFlush(_buffer.Count, currentBytes))
                {
                    _flushRequested = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] TrackAutomated({eventName}) failed: {e.Message}");
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
            _samplingPolicy.SetEventRate(TruncateEventName(eventName), rate);
        }

        /// <summary>
        /// Remove a per-event-name sampling override so the event falls back to the global rate.
        /// Returns true if an override was present.
        /// </summary>
        public bool RemoveEventSamplingRate(string eventName)
        {
            if (!_initialized) return false;
            return _samplingPolicy.RemoveEventRate(TruncateEventName(eventName));
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
                StartCoroutine(FlushCoroutine(_buffer.DequeueAll()));
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _isFlushing, 0);
                Debug.LogError($"[Framedash] Flush() failed: {e.Message}");
            }
        }

        private IEnumerator FlushCoroutine(TelemetryEvent[] events)
        {
            try
            {
                yield return _transport.SendBatch(events);
            }
            finally
            {
                Interlocked.Exchange(ref _isFlushing, 0);
            }
        }

        /// <summary>Shutdown the SDK gracefully.</summary>
        public void Shutdown()
        {
            try
            {
                if (!_initialized) return;
                if (_flushCoroutine != null) StopCoroutine(_flushCoroutine);
                Flush();
                _initialized = false;
                Debug.Log("[Framedash] SDK shut down.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Framedash] Shutdown() failed: {e.Message}");
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
            if (pauseStatus) Flush();
        }

        private void OnApplicationQuit()
        {
            Shutdown();
        }

        private void OnDestroy()
        {
            if (s_instance == this)
            {
                Shutdown();
                s_instance = null;
            }
        }
    }
}
