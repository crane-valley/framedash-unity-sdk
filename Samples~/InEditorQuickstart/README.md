# In-Editor Quickstart

Activate your Framedash project straight from the Unity Editor's **Play mode** --
no CI build and no real players required.

Your project "activates" on its **first real, map-qualified spatial event**. The
SDK's automatic performance heartbeat sends an empty `map_id` and does **not**
count; only an explicit `Track(eventName, mapId)` with a non-empty, **registered**
`map_id` does. This sample sends exactly that.

## Steps (about two minutes, all in the Editor)

1. **Register a map.** In the Framedash dashboard open your project's **Maps** page
   and either click **Generate demo** (fastest) or upload a map image. Copy one
   `map_id` from the Maps list. The heatmap returns 404 for an unknown map, so the
   `map_id` must already exist.
2. **Add the component.** Create an empty GameObject in any scene and add
   **Framedash > Framedash Quickstart** (the `FramedashQuickstart` script).
3. **Configure it.** Paste an **Ingest key** into `Api Key` and the `map_id` into
   `Map Id`. The key must have the `events:write` scope -- in the dashboard create a
   new API key with the **Ingest** preset; a read/admin key without `events:write`
   is rejected by ingest and nothing appears.
4. **Press Play.** One map-qualified event is sent automatically -- your project
   activates. Press the **Send Key** (default `Space`) to drop additional points;
   move the GameObject between presses to spread them across the heatmap. (The
   send-key shortcut needs the legacy **Input Manager**; on Input-System-only
   projects it is disabled with a one-time console note, but the automatic
   Play-mode activation still works.)
5. **See it.** Open that map's **heatmap** in the dashboard -- your point(s) appear.

## Notes

- This emits **real** events (not demo/synthetic data), so they count toward
  activation -- unlike the dashboard "Generate demo" button.
- **Editor-only logic:** every member is wrapped in `#if UNITY_EDITOR`, so the
  telemetry-sending code is **stripped from player builds** -- it can never ship in a
  game or send telemetry from a build. The empty class shell is kept in builds on
  purpose, so a component accidentally left on a GameObject stays inert rather than
  becoming a "Missing (Mono Script)" reference. It runs fully in Editor Play mode.
- Fail-safe: a missing `Api Key` or `Map Id` only logs a warning (and sends no
  telemetry at all), and the sample never throws into your game loop. Values pasted
  with stray whitespace are trimmed.
- Input System: the automatic Play-mode activation works either way. The optional
  **Send Key** shortcut uses the legacy **Input Manager**; on a project set to the
  **Input System package** exclusively it is disabled (the sample catches the
  exception, warns once, and stops trying) rather than spamming the console.
- Position: the event is sent at the GameObject's transform position, so it must fall
  inside your map's world bounds to show on the heatmap. The demo maps contain the
  origin, so an empty GameObject at `(0,0,0)` works; for your own uploaded map, move
  the GameObject within that map's bounds (and between sends to spread points).
- Already set up? If the Framedash SDK is already initialized elsewhere in the
  project, this component's `Api Key` is ignored (the existing configuration wins) and
  the sample logs a warning. Use it in a project where the SDK is not yet set up.
- This is a quickstart, not a production integration. For the full setup (player
  identity, build ids, automatic performance capture) see the package README and
  https://docs.framedash.dev/en/sdk/unity.
