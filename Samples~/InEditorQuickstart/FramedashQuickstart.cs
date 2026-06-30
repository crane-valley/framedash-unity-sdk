// The quickstart LOGIC is Editor-only: every member is wrapped in UNITY_EDITOR, so the
// telemetry-sending code is stripped from player builds -- it can never ship in a game
// or emit telemetry from a build. The class SHELL (an empty MonoBehaviour) and its
// namespace are kept in player builds on purpose, so that a component accidentally left
// on a GameObject in a built scene/prefab stays an inert empty component rather than
// becoming a "Missing (Mono Script)" reference. In Editor Play mode (where UNITY_EDITOR
// is defined) the full quickstart runs, which is the point.
using UnityEngine;
#if UNITY_EDITOR
using Framedash;
#endif

namespace FramedashSamples
{
    /// <summary>
    /// Framedash in-editor quickstart (the logic is Editor-only).
    ///
    /// Goal: ACTIVATE your project (send its first real, map-qualified spatial event)
    /// straight from the Unity Editor's Play mode -- no CI build, no real players. The
    /// SDK's automatic performance heartbeat sends an EMPTY map_id and does NOT count
    /// toward activation; only an explicit Track(eventName, mapId) with a non-empty,
    /// REGISTERED map_id does. This sample sends exactly that.
    ///
    /// Setup (all in the Editor, about two minutes):
    ///   1. In the Framedash dashboard, open your project -> Maps and either click
    ///      "Generate demo" (fastest) or upload a map image. Copy one map_id from the
    ///      Maps list -- the heatmap 404s on an unknown map, so it MUST already exist.
    ///   2. Create an empty GameObject in your scene and add this component.
    ///   3. Paste an Ingest API key (events:write scope) into "Api Key" and the map_id
    ///      into "Map Id".
    ///   4. Press Play. One map-qualified event is sent automatically (your project
    ///      activates). Press the Send Key to drop additional points on the heatmap.
    ///   5. Open that map's heatmap in the dashboard -- your point(s) appear in seconds.
    ///
    /// Fail-safe by design (matches the SDK contract): a missing field only logs a
    /// warning, nothing here can throw out into your game loop, and the logic is
    /// UNITY_EDITOR-only so it never runs in a player build.
    /// </summary>
    [AddComponentMenu("Framedash/Framedash Quickstart")]
    public sealed class FramedashQuickstart : MonoBehaviour
    {
#if UNITY_EDITOR
        [Tooltip(
            "An INGEST API key for your project -- it needs the events:write scope "
                + "(dashboard -> project -> API keys -> new key, 'Ingest' preset). A read/admin "
                + "key without events:write is rejected by ingest and nothing appears. Required.")]
        [SerializeField]
        private string apiKey = "";

        [Tooltip(
            "A map_id already registered in your project (dashboard -> Maps). The heatmap "
                + "returns 404 for an unknown map, so this MUST be an existing map_id. Required.")]
        [SerializeField]
        private string mapId = "";

        [Tooltip("Event name to send. Any non-empty name works.")]
        [SerializeField]
        private string eventName = "quickstart_ping";

        [Tooltip(
            "Press this key in Play mode to send another map-qualified event. Requires the "
                + "legacy Input Manager; ignored on Input-System-only projects.")]
        [SerializeField]
        private KeyCode sendKey = KeyCode.Space;

        // Trimmed, validated copies used at runtime, so a value pasted from the dashboard
        // with stray whitespace still works and the serialized Inspector fields are left
        // untouched.
        private string _apiKey;
        private string _mapId;
        private string _eventName;
        private bool _ready;

        // Set once UnityEngine.Input has thrown (new Input System exclusively), so the
        // send-key path is disabled and the warning is logged only once -- not per frame.
        private bool _sendKeyUnavailable;

        private void Start()
        {
            _apiKey = (apiKey ?? "").Trim();
            _mapId = (mapId ?? "").Trim();
            // Track() drops a whitespace event_name; fall back to the default so the
            // "Activated" log below is never printed for an event that was silently dropped.
            _eventName = string.IsNullOrWhiteSpace(eventName) ? "quickstart_ping" : eventName.Trim();

            // Validate BOTH required fields BEFORE Initialize: TelemetrySDK.Initialize
            // immediately enqueues a real session_start event and starts the perf
            // heartbeat, so initializing without a map_id would emit real but
            // non-map-qualified telemetry that consumes quota without activating. A
            // missing required field must therefore emit no telemetry at all.
            if (string.IsNullOrEmpty(_apiKey))
            {
                Debug.LogWarning(
                    "[Framedash Quickstart] Set 'Api Key' on this component, then press Play.");
                return;
            }

            if (string.IsNullOrEmpty(_mapId))
            {
                Debug.LogWarning(
                    "[Framedash Quickstart] Set 'Map Id' to a map registered in your project "
                        + "(dashboard -> Maps), then press Play.");
                return;
            }

            if (TelemetrySDK.Instance.IsInitialized)
            {
                // Already initialized (another bootstrap script, or a TelemetrySDK component in
                // the scene). Do NOT call Initialize: it overwrites the live singleton's apiKey
                // / playerId fields while InitializeInternal() no-ops, which would leave the
                // transport on the old config AND clobber the user's setup. Use the existing
                // configuration instead; this component's Api Key is intentionally ignored.
                Debug.LogWarning(
                    "[Framedash Quickstart] The Framedash SDK is already initialized in this "
                        + "project, so this component's 'Api Key' is ignored -- the event is sent "
                        + "with the existing configuration. Run the quickstart in a project where "
                        + "the SDK is not already set up to use this Api Key.");
            }
            else
            {
                // A stable demo player_id keeps the events attributed and skips the SDK's
                // anonymous-player warning; a real integration calls SetPlayerId with its own id.
                TelemetrySDK.Initialize(
                    apiKey: _apiKey,
                    buildId: Application.version,
                    playerId: "quickstart-player");
            }

            _ready = true;

            // Send one map-qualified event immediately so simply pressing Play activates
            // the project (its first spatial-heatmap event).
            Send();
            Debug.Log(
                $"[Framedash Quickstart] Activated: sent '{_eventName}' on map '{_mapId}'. "
                    + $"Press {sendKey} for more points, then open that map's heatmap.");
        }

        private void Update()
        {
            if (!_ready || _sendKeyUnavailable)
            {
                return;
            }

            bool pressed;
            try
            {
                pressed = Input.GetKeyDown(sendKey);
            }
            catch (System.InvalidOperationException)
            {
                // A project configured for the new Input System exclusively (Active Input
                // Handling = "Input System Package") throws on the legacy UnityEngine.Input
                // every frame. Disable the send-key path and warn ONCE rather than spamming
                // the console; activation already happened on Play, only the extra-points
                // shortcut is unavailable.
                _sendKeyUnavailable = true;
                Debug.LogWarning(
                    "[Framedash Quickstart] The send-key shortcut needs the legacy Input "
                        + "Manager, but this project uses the Input System package, so it is "
                        + "disabled. Your project already activated on Play.");
                return;
            }

            if (pressed)
            {
                Send();
            }
        }

        private void Send()
        {
            // The NON-EMPTY mapId is what makes this event map-qualified -- the event that
            // activates the project. position gives the heatmap a spatial point (move this
            // GameObject between presses to spread the points out).
            TelemetrySDK.Instance.Track(_eventName, _mapId, transform.position);

            // Quickstart only: push the event out immediately so it reaches the heatmap in
            // seconds instead of waiting for the periodic flush. A real integration lets the
            // SDK batch -- it flushes on its own at 100 events / 30s / shutdown.
            TelemetrySDK.Instance.Flush();
        }
#endif
    }
}
