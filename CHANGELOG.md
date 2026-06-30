# Changelog

All notable changes to the Framedash Unity SDK are documented here. This project
follows [Keep a Changelog](https://keepachangelog.com/) and
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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
