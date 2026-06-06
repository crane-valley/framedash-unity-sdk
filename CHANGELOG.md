# Changelog

All notable changes to the Framedash Unity SDK are documented here. This project
follows [Keep a Changelog](https://keepachangelog.com/) and
[Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-06-06

Initial public pre-release (beta).

- Unity telemetry SDK: `TelemetrySDK.Initialize(apiKey, endpointUrl, buildId)` and
  `TelemetrySDK.Instance.Track(...)`.
- Automatic performance collection (FPS, frame time, memory) and session lifecycle.
- Batched, gzip-compressed HTTP transport with retry and an offline queue.
- Hand-written Protobuf serialization (no codegen dependency).
