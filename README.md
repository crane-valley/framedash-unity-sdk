# Framedash Unity SDK

Unity UPM package for collecting game telemetry and sending it to the Framedash platform.

## Requirements

- Unity 2022.3+

## Installation

Add via Unity Package Manager using the git URL:

```
https://github.com/crane-valley/framedash-unity-sdk.git
```

To pin a release, append a tag, e.g. `https://github.com/crane-valley/framedash-unity-sdk.git#v0.1.1`.

## In-Editor Quickstart (fastest first activation)

Want real data on a heatmap before wiring the SDK into your game? The **In-Editor
Quickstart** sample activates your project from Play mode in about two minutes --
no CI build and no real players.

Your project activates on its first **map-qualified** event: an explicit
`Track(eventName, mapId)` with a non-empty, registered `map_id`. (The automatic
performance heartbeat sends an empty `map_id` and does not count.)

1. **Register a map.** In the dashboard, open your project's **Maps** page and click
   **Generate demo** (or upload a map image). Copy a `map_id` from the list -- the
   heatmap 404s on an unknown map, so it must already exist.
2. **Import the sample.** In **Package Manager -> Framedash Telemetry SDK ->
   Samples**, click **Import** next to *In-Editor Quickstart*.
3. **Add + configure.** Add the `FramedashQuickstart` component
   (**Framedash > Framedash Quickstart**) to an empty GameObject, then set its
   **Api Key** (an **Ingest** key -- it needs the `events:write` scope; a read/admin
   key is rejected by ingest) and **Map Id**.
4. **Press Play.** One map-qualified event is sent automatically (your project
   activates); press the send key for more points (the send-key shortcut needs the
   legacy Input Manager; on Input-System-only projects it is disabled, but the
   automatic activation still works). Open that map's heatmap in the dashboard to
   see them.

These are **real** events (not the dashboard's synthetic "Generate demo" data), so
they count toward activation. The sample's logic is **Editor-only** (every member is
wrapped in `#if UNITY_EDITOR`), so it is stripped from player builds and never sends
telemetry from a game; only an inert empty component remains if you leave it attached.
See `Samples~/InEditorQuickstart/README.md` for the full walkthrough.

## Components

| File | Description |
|------|-------------|
| `TelemetrySDK.cs` | Main entry point — initialization, configuration, session lifecycle |
| `TelemetryEvent.cs` | Event data model |
| `TelemetrySerializer.cs` | Event serialization |
| `ProtobufWriter.cs` | Protobuf binary encoding |
| `TransportLayer.cs` | HTTPS transport with gzip compression |
| `SessionManager.cs` | Session ID and metadata management |
| `PerformanceCollector.cs` | Automatic FPS, frame time, GPU time, memory collection |
| `SamplingPolicy.cs` | Configurable event sampling and throttling |
| `EventBuffer.cs` | Batched event buffering before transmission, default 10,000 events |

## Automatic Events

The SDK automatically sends the following events with `Source=Automated`. These bypass sampling policy and always fire regardless of the configured sampling rate.

| Event | Trigger | Description |
|-------|---------|-------------|
| `session_start` | Once, on initialization | Guarantees the backend sees at least one event per session |
| `perf_heartbeat` | Every 10 seconds | Continuous performance baseline (FPS, frame time, GPU time, memory) |

Both events include full performance metrics from `PerformanceCollector`. The heartbeat timer uses `Time.unscaledDeltaTime`, so it continues during `timeScale=0` (pause menus).

## Performance Collection

`PerformanceCollector.cs` uses `Time.unscaledDeltaTime * 1000f` for `frame_time_ms` (timeScale-independent). See [Frame Timing Metrics Guide](../../docs/en/frame-timing-metrics.md) for details on available metrics and collection APIs.

## Field Limits

The ingest pipeline validates every event and rejects the **entire batch** if any single field is out of range -- *after* returning 202, so the drop is silent. To prevent one oversized value from dropping unrelated events in the same flush, the SDK clamps each per-event field client-side (in `FieldClamp.cs`, with `player_id` normalized in `SessionManager.cs`) before buffering:

| Field | Limit | Over-limit behavior |
|-------|-------|---------------------|
| `event_name` | 128 chars | Truncated |
| `map_id` | 128 chars | Truncated |
| `build_id` | 128 chars | Truncated |
| `player_id` | 128 chars | Trimmed, then truncated |
| `position` (x/y/z) | finite, \|v\| ≤ 1e9 | NaN/Inf → 0; magnitude clamped to ±1e9 |
| Attributes | ≤ 50 entries; key ≤ 64 chars; value ≤ 512 chars | Excess entries and empty/null keys dropped; key/value truncated |
| Metrics | ≤ 50 entries; key ≤ 64 chars; value must be finite | Excess entries, empty keys, and NaN/Inf values dropped; key truncated |
| `fps` | 0–1000 | Derived from the raw frame delta; capped at 1000 |
| `frame_time_ms` / `gpu_time_ms` / `game_thread_ms` / `render_thread_ms` | 0–10000 | NaN/negative → 0; capped at 10000 |
| `memory_used_bytes` | 0–64 GiB | Negative → 0; above 64 GiB capped |
| `platform` / `engine_version` | 64 chars | Auto-collected; truncated |

Whitespace-only event names are also dropped (ingest requires a non-empty name).

## Camera Direction

When **Capture Camera Rotation** is enabled (the default), every event records the main camera's yaw and pitch, which powers the direction breakdown on the heatmap cell-detail view. The SDK samples `Camera.main` once per frame and stamps events with that value (the same per-frame caching used for performance metrics); like all SDK methods, `Track()` is intended to be called on Unity's main thread. If no camera tagged `MainCamera` exists (for example a headless or dedicated build), the fields are simply omitted. Yaw is normalized to `[0, 360)` and increases clockwise; the direction chart labels yaw 0 as North, with the engine's forward axis as that reference (a game world has no geographic North, so the compass labels are relative). Pitch is `[-90, 90]` (+90 = looking up).

Disable it by unchecking **Capture Camera Rotation** on the `TelemetrySDK` component inspector, or from code (including the `TelemetrySDK.Initialize(...)` path) via `TelemetrySDK.Instance.CaptureCameraRotation = false;`.

## Quick Start

### 1. Initialize once at startup

Attach `TelemetrySDK` to a persistent GameObject and fill the serialized fields,
or initialize it from code:

```csharp
using Framedash;
using UnityEngine;

public sealed class GameBootstrap : MonoBehaviour
{
    private void Start()
    {
        TelemetrySDK.Initialize(
            apiKey: "your-api-key",
            endpointUrl: "https://ingest.framedash.dev/v1/events",
            buildId: Application.version
        );
    }
}
```

### 2. Track gameplay events

```csharp
TelemetrySDK.Instance.Track(
    eventName: "player_death",
    mapId: "map_01",
    position: playerTransform.position
);
```

### 3. Set the player ID after login (optional)

```csharp
TelemetrySDK.Instance.SetPlayerId(playerId);
```

### 4. Sampling

The SDK applies a global sampling rate (default `1.0` = keep all) configured
via the `_samplingRate` inspector field. High-frequency events can opt into a
lower per-event-name rate that overrides the global rate at runtime:

```csharp
TelemetrySDK.Instance.SetEventSamplingRate("ai_pathfind_step", 0.05f); // ~5%
TelemetrySDK.Instance.RemoveEventSamplingRate("ai_pathfind_step");      // back to global
```

Automatic events (`session_start`, `perf_heartbeat`) bypass sampling.

### 5. Automated profiling sessions (CI)

For build-over-build performance gating, tag a run's events with build metadata
so the dashboard and `framedash perf-diff` can compare one build against another.
Call this once after `Initialize()` in your automated-test / profiling entry point:

```csharp
TelemetrySDK.Instance.BeginAutomatedSession(
    buildId:  commitSha,       // -> the first-class build_id field
    branch:   "main",          // -> ci.branch attribute
    commit:   commitSha,       // -> ci.commit attribute
    scenario: "boot_to_menu"); // -> ci.scenario attribute

// ... run the scenario; gameplay + perf_heartbeat events are now tagged ...

TelemetrySDK.Instance.Flush();
TelemetrySDK.Instance.EndAutomatedSession();
```

`branch`, `commit`, and `scenario` ride in the existing event `attributes` map
(`ci.*`), so no schema change is required; the tags apply to every event,
including the automatic `perf_heartbeat` that carries the frame-time / memory /
GPU metrics. A per-event attribute with the same key overrides the session value.

If your CI harness exports the standard Framedash variables (`FRAMEDASH_BUILD_ID`,
`FRAMEDASH_GIT_BRANCH`, `FRAMEDASH_GIT_COMMIT`, `FRAMEDASH_TEST_SCENARIO`) -- the
planned `framedash run-profile-test` runner will export these for you -- call the
zero-argument overload instead:

```csharp
TelemetrySDK.Instance.BeginAutomatedSessionFromEnvironment();
```

Then gate the build in CI with
`framedash perf-diff --baseline <old_build_id> --candidate <new_build_id> --fail-on-regression`.

Two things to know when wiring this into a real pipeline:

- `build_id` is the dimension `perf-diff` compares. It groups and compares by
  `build_id` (optionally narrowed by map/platform), not by `ci.scenario`, so two
  scenarios under one `build_id` fold into a single aggregate. To compare
  scenarios independently, give each its own `build_id` (for example
  `<commit>-<scenario>`) and treat `ci.scenario` as a queryable label rather than
  a `perf-diff` split key.
- The `ci.*` tags live in the event `attributes` map, which COPPA-redacted
  projects strip on ingest -- under COPPA only `build_id` survives. If you run
  automated profiling on a COPPA project, make `build_id` carry everything the
  comparison must distinguish.

## Offline Queue

Telemetry that cannot be delivered is not lost. When a batch still fails after
its retries, or when events are still buffered as the app shuts down, the SDK
writes them to a small file under `Application.persistentDataPath` and resends
them on the next run.

- Enabled by default. Toggle it with the **Enable Offline Queue** inspector
  field on the `[Framedash]` component, or, for code-only setups, pass
  `enableOfflineQueue: false` to `TelemetrySDK.Initialize(...)`.
- The on-disk queue is capped at 1,000 events; once full, the oldest are dropped
  first. The file is rewritten atomically, so a crash never corrupts it.
- The queue file is partitioned by the ingest configuration (endpoint + API
  key), so moving the same install between local/staging/prod -- or rotating
  keys -- never resends one project's events to another.
- Only transient failures are queued. A permanent client error (a bad API key,
  a wrong path, a 4xx other than rate-limiting) drops the batch instead of
  queuing a payload that could never succeed.
- Restored events are sent before new ones. Once the server confirms delivery
  they are removed from disk; only genuinely undelivered events are kept. An
  acknowledged event is never resent, and an undelivered one is never lost
  (a rare duplicate is only possible if a single oversized batch is split and
  delivered in part).
- Turn the flag off for a pure in-memory buffer with no disk writes (events
  buffered at shutdown are then dropped, as before).

A best-effort note: a batch that is mid-flight over the network at the exact
moment the process is force-killed is not captured (the same limitation as any
in-flight HTTP request). The queue covers the common cases -- retry exhaustion
and graceful shutdown.

## Local Development

To point the Unity SDK at a local Framedash stack:

1. Follow the root [local setup guide](../../README.md) to start Docker services. Then create the local environment files from the checked-in examples:
   ```bash
   cp apps/web/.env.example apps/web/.env.local
   cp apps/ingest/.dev.vars.example apps/ingest/.dev.vars
   cp apps/consumer/.dev.vars.example apps/consumer/.dev.vars
   ```
   Edit these files as described in the root setup guide.
2. Start the ingest Worker locally:
   ```bash
   pnpm --filter @framedash/ingest dev
   ```
3. Initialize the SDK with the localhost ingest endpoint:
   ```csharp
   TelemetrySDK.Initialize(
       apiKey: "your-local-api-key",
       endpointUrl: "http://localhost:8787/v1/events",
       buildId: Application.version
   );
   ```

The local API key must exist in your local PostgreSQL `api_keys` table. Create
one via the dashboard at `http://localhost:3000`.
