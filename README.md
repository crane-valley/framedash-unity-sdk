# Framedash Unity SDK

Unity UPM package for collecting game telemetry and sending it to the Framedash platform.

## Requirements

- Unity 2022.3+

## Installation

Add via Unity Package Manager using the git URL:

```
https://github.com/crane-valley/framedash-unity-sdk.git
```

To pin a release, append a tag, e.g. `https://github.com/crane-valley/framedash-unity-sdk.git#v0.1.0`.

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
