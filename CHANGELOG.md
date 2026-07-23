# Changelog

All notable changes to the Framedash Unity SDK are documented here. This project
follows [Keep a Changelog](https://keepachangelog.com/) and
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.7] - 2026-07-24

### Added

- The in-editor SceneView heatmap now requests opt-in XYZ aggregation and renders
  measured telemetry height as translucent voxels using the same five-stop
  blue-to-red palette as the UE5 editor overlay. Older API responses without
  `z` remain visible as flat cells at the configured map floor.
- A SceneView **Framedash Heatmap** overlay now exposes independent **Show**,
  **Frame**, and **Controls** actions. The controls include a Frame Heatmap button,
  a five-stop intensity legend, cell count, and maximum weight.

### Changed

- The selected map and overlay preference now persist per project. The controls
  restore the map list after reopening and refetch an enabled heatmap after script
  reloads, while closing the controls window no longer destroys the SceneView mesh.
- Map selection and refresh controls now stack vertically, preserving full map
  names in narrow docked layouts, and the cached map-label array avoids repaint-time
  allocations.

### Fixed

- The standalone NUnit project writes build artifacts under Unity-ignored
  `Tests/.dotnet/`, preventing a local `dotnet test` run from importing test-host
  assemblies into a consuming Unity project.

## [0.1.6] - 2026-07-22

### Added

- The editor heatmap can read its `analytics:read` key from
  `FRAMEDASH_ANALYTICS_API_KEY` when the per-project Read API Key is empty. The
  environment value is used only by the current Unity process and is never
  persisted under `UserSettings/`; an explicitly configured key still wins.

## [0.1.5] - 2026-07-20

### Fixed

- A full in-memory buffer no longer evicts restored offline-queue events before
  they are acknowledged. While a durable prefix is pending, new events are
  rejected at capacity so positional disk acknowledgements cannot target the
  wrong events or make shutdown discard an unpersisted fresh tail.

## [0.1.4] - 2026-07-17

### Added

- Memory-category metrics: the SDK auto-attaches `mem.vram` (allocated
  graphics-driver memory via `Profiler.GetAllocatedMemoryForGraphicsDriver`)
  and `mem.heap` (managed heap via `Profiler.GetMonoUsedSizeLong`) to
  `perf_heartbeat` metrics and to position-qualified events (non-empty
  `map_id`; a cached sample refreshed at heartbeat cadence -- the event path
  does no engine reads). Absent means not collected: a zero reading is
  omitted, never emitted as a fabricated 0. Caller-supplied metric keys
  always win, both on key collision and on capacity -- `mem.*` fills only
  the remaining slots below the 50-metric ingest cap, with `mem.vram`
  taking priority when capacity is partial, so an event that already
  carries 50 caller metrics behaves exactly as before.
- In-editor SceneView cloud heatmap overlay: a new editor-only
  `Framedash.Editor` assembly fetches the project's maps and aggregated
  heatmap cells from the Framedash REST API (requires a Read API Key with
  the `analytics:read` scope plus the Project ID -- never the game's
  write-only ingest key) and renders translucent heatmap quads in the
  SceneView at the recorded world coordinates. Overlay settings persist
  per-project via `ScriptableSingleton` under `UserSettings/` (never
  packaged, never VCS-tracked). Editor HTTP fetches are bounded by a 30s
  timeout, and quad geometry is built once per data load into a cached
  mesh drawn with a single call per repaint.

## [0.1.3] - 2026-07-12

### Added

- Map/level load-time capture: `BeginMapLoad(mapName)` / `EndMapLoad()` time a
  load on a monotonic, time-scale- and pause-safe clock, and
  `ReportMapLoad(mapName, loadTimeMs)` lets self-measured loaders report a load
  time directly. Both paths emit a `map_load` auto event carrying
  `metrics["load_time_ms"]` and `attributes["map_name"]`; `map_id` is
  deliberately left empty so the event stays out of the spatial heatmap grid
  and the activation gate. `ReportMapLoad` drops (does not clamp) a NaN,
  Infinity, or negative `loadTimeMs`. Calling `BeginMapLoad` again before
  `EndMapLoad` replaces the pending measurement. Main-thread only; fail-safe
  (never throws, no-op if the SDK is not initialized).
- `io.*` disk metrics: the SDK auto-attaches `io.read_bytes` / `io.read_time_ms`
  / `io.read_ops` (deltas since the previous heartbeat) to `perf_heartbeat`
  metrics, sourced from Unity's `AsyncReadManagerMetrics` API in editor and
  development builds only (unavailable in release players, where only the
  manual feed contributes). `ReportIoSample(bytes, readTimeMs, ops)` is
  available on every build target for developers who want to feed their own
  disk-I/O reads; the attach only happens once a sample -- automatic or
  manual -- has actually landed (no zero-stuffing for unused feeds).

## [0.1.2] - 2026-07-05

### Added

- Opt-in verbose transport logging (F29, parity with the UE5 SDK's transport
  logging): with `TelemetrySDK.VerboseLogging` enabled (Inspector toggle or the
  `VerboseLogging` property, with live propagation to the transport; default off),
  each send attempt logs the endpoint and gzip-compressed payload size, and each
  accepted batch logs "Flushed N events (HTTP 202)" so first-time integrators can
  positively confirm delivery client-side.
- API-key environment-variable fallback for CI (F32): when no key is configured
  via the Inspector or `TelemetrySDK.Initialize(apiKey, ...)`, the SDK falls back
  to the `FRAMEDASH_API_KEY` environment variable (`ApiKeyResolver`), so a CI
  build can authenticate without hardcoding a secret in the project. An
  explicitly configured key always wins, matching the CLI's `--api-key` vs
  `FRAMEDASH_API_KEY` precedence contract; the env read is lazy and fail-safe
  (a sandboxed runtime that throws on environment access degrades to "no env
  key" instead of surfacing an exception).
- Prefer-IPv4-with-IPv6-fallback ingest connect (parity with the Godot SDK). When
  the primary UnityWebRequest attempt fails at the transport level (network error /
  timeout, `responseCode` 0), the transport resolves the endpoint to concrete IPv4
  and IPv6 addresses and retries within the same flush over a direct TLS socket
  pinned to the IPv4 literal first, falling back to IPv6 (and toggling family
  across retries) only if IPv4 also fails at the transport level. TLS is
  authenticated against the original hostname via
  `SslStream.AuthenticateAsClient(fqdn)`, so SNI and full standard certificate
  validation (chain, expiry, hostname) are preserved -- no custom certificate
  logic -- and the request carries a `Host: <fqdn>` header so routing is unchanged.
  This turns the previous fail-fast + persist behavior into active in-flush
  delivery on broken-IPv6 networks (a global AAAA advertised via Router
  Advertisement with no working IPv6 route). Loopback, IP-literal, and plain-HTTP
  endpoints are unaffected (passthrough), a total DNS failure falls back to the
  previous behavior, and WebGL builds are exempt (no sockets in the browser
  sandbox; the offline queue remains the safety net there).
  Attempt accounting: the retry budget (`RetryPolicy.MaxRetries`) bounds PRIMARY
  UnityWebRequest attempts, and each transport-level primary failure may add one
  direct-socket fallback POST within the same attempt -- worst case (total
  blackout, both paths timing out every attempt) is 2 x MaxRetries POSTs and
  roughly 20s wall time per attempt, in exchange for in-flush delivery whenever
  either path works.

### Changed

- Transport whole-request timeout bounded 30s -> 10s. On a broken-IPv6 network (a
  global AAAA advertised via Router Advertisement with no working route)
  UnityWebRequest has no Happy Eyeballs, so an OS-resolver AAAA-first pick wedges
  each flush connect; the shorter timeout fails fast instead of stalling 30s. The
  offline queue (on by default) keeps the timed-out batch so it is retried on the
  next run/initialization. Paired with the direct-socket IPv4 fallback above, the
  timeout now bounds each primary attempt before the same flush actively retries
  over IPv4; the persisted queue is the safety net only when both paths fail.
- The SDK version reported via `X-SDK-Version` is now a code constant instead of
  a serialized Inspector field. A `[SerializeField]` version was captured into
  scenes/prefabs, so upgrading the package kept sending the OLD version from the
  saved scene; the constant always matches the installed package.

## [0.1.1] - 2026-06-30

### Added

- Automated profiling sessions for CI: `BeginAutomatedSession(buildId, branch,
  commit, scenario)` (and `BeginAutomatedSessionFromEnvironment()`, which reads the
  `FRAMEDASH_BUILD_ID` / `FRAMEDASH_GIT_BRANCH` / `FRAMEDASH_GIT_COMMIT` /
  `FRAMEDASH_TEST_SCENARIO` environment variables) tag every subsequent event with
  CI metadata so build-over-build performance can be compared in the dashboard and
  via `framedash perf-diff`. The build id is stamped as the first-class `build_id`
  field; branch, commit, and scenario ride in the existing attributes map as
  `ci.branch` / `ci.commit` / `ci.scenario` (no proto change). `EndAutomatedSession()`
  stops the tagging. The session tags merge into every event -- including the
  automatic `perf_heartbeat` that carries the performance metrics -- and a per-event
  attribute with the same key overrides the session value.
- On-disk offline queue (enabled by default; toggle "Enable Offline Queue" in the
  inspector, or pass `enableOfflineQueue` to `TelemetrySDK.Initialize(...)` for
  code-only setups). Events that cannot be sent -- transient network failures
  after retries, or events still buffered when the app shuts down -- are written
  to a small file under `Application.persistentDataPath` and retried on the next
  run instead of being lost. A permanent client error (bad API key, wrong path)
  drops the batch rather than queuing an undeliverable payload. The queue is
  capped at 1,000 events (oldest dropped first), rewritten atomically, and
  partitioned by ingest configuration so switching endpoint/key never resends one
  project's events to another. Restored events are sent first; once the server
  confirms delivery they are removed from disk, and only genuinely undelivered
  events are kept, so an event is never lost and is not re-sent once
  acknowledged. Set the flag off for a pure in-memory buffer with no disk writes.
- In-editor quickstart sample ("In-Editor Quickstart", listed under Package Manager
  -> Samples). The `FramedashQuickstart` MonoBehaviour initializes the SDK and emits
  a map-qualified event from Play mode, so a project activates (its first spatial
  heatmap event) without a CI build: press Play to send one automatically, then the
  send key for more. These are real events, not the dashboard's synthetic demo data,
  so they count toward activation. The logic is Editor-only (members wrapped in
  `#if UNITY_EDITOR`), so it is stripped from player builds and never sends from a
  game; the empty class shell stays so a forgotten component is inert, not a missing
  script.

### Changed

- The SDK now clamps every per-event field to the ingest server limits
  client-side before buffering. The ingest validator rejects the entire batch if
  any single event field exceeds a limit, so one over-length attribute or one
  non-finite coordinate would previously drop every event in that flush. Event
  name, map ID, build ID, and player ID are truncated; positions are made finite
  and bounded; attributes and metrics are capped in count and length (non-finite
  metric values dropped); FPS and timing metrics are clamped to their valid
  ranges. See the README "Field Limits" section for the exact caps.

### Fixed

- `Track()` now drops whitespace-only event names (previously only null/empty
  were rejected). This is an SDK-level data-quality tightening: ingest only
  requires a non-empty name, so a whitespace-only name would otherwise be sent
  but carries no analytic value.
- The per-frame `Update`, camera-sampling, and lifecycle callbacks
  (`OnApplicationPause`/`OnApplicationQuit`/`OnDestroy`) now swallow exceptions,
  upholding the never-throw fault-safety contract.
- Retry backoff now uses real (unscaled) time, so a paused app or
  `Time.timeScale == 0` no longer stalls transport retries.
- Re-initializing after `Shutdown()` now resets the flush state. A flush left
  in flight by the previous session no longer wedges the single-flight guard
  (which would otherwise silently block every flush of the new session,
  including `session_start`), and a stale flush completing after re-init no
  longer releases the new session's guard early (a flush-generation check keeps
  the two sessions' flushes from overlapping).

## [0.1.0] - 2026-06-06

Initial public pre-release (beta).

- Unity telemetry SDK: `TelemetrySDK.Initialize(apiKey, endpointUrl, buildId)` and
  `TelemetrySDK.Instance.Track(...)`.
- Automatic performance collection (FPS, frame time, memory) and session lifecycle.
- Batched, gzip-compressed HTTP transport with retry and an offline queue.
- Hand-written Protobuf serialization (no codegen dependency).
