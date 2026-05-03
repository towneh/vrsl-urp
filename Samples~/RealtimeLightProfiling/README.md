# Realtime Light Profiling

Editor utilities and a synthetic DMX source for benchmarking the realtime
light path under matched conditions.

## What this sample provides

- **`VRSL → Profiling → Build Profiling Scene`** — opens the *Profiling Scene
  Builder* editor window. The window builds (or rebuilds) a deterministic
  profiling scene with N matched fixtures on a horizontal truss, a flat floor,
  a fixed-pose camera, and the synthetic DMX source wired up. Switching fixture
  count / camera variant / lighting path rebuilds the truss in place — no need
  to re-author scenes by hand for every cell of the sweep table.
- **`VRSLProfilingSyntheticDMXSource`** — a runtime `MonoBehaviour` that
  bypasses VRSL's CRT decode chain. It writes a small CPU-authored pixel buffer
  matching the format that fixture shaders consume after CRT decode and
  publishes it as both the legacy `_Udon_DMXGrid…` globals and the URP
  manager's texture references. With the synthetic source active, the
  GridReader camera and CustomRenderTexture chain are not needed, so frame-to-
  frame variance from video decode and CRT scheduling is eliminated.

## Requirements

- Unity 6.
- URP 17+ if you want to profile the URP realtime light path. Without URP, the
  builder window still opens — the **Lighting path** dropdown grays out
  `URPRealtime` and only `LegacyMeshShader` is selectable, so legacy-only
  projects can still benchmark the legacy volumetric/projector path.

## Quick start

1. Run `VRSL → Profiling → Import Profiling Sample` (or, if
   you prefer the long way: Package Manager → VRSL URP → Samples →
   **Import** next to *Realtime Light Profiling*).
2. Create or open an empty scene to use as your profiling scene.
3. Open `VRSL → Profiling → Build Profiling Scene`.
4. Pick the lighting path (`URPRealtime` or `LegacyMeshShader`), fixture count,
   and camera variant, then click **Build / Rebuild Profiling Scene**.
5. Save the scene with a descriptive name (e.g. `Profile-URP-50.unity`).

To run a sweep (10 → 25 → 50 → 100 → 200 fixtures), open the builder, change
the fixture count, click **Build / Rebuild**, and re-record. The truss is
rebuilt in place; the camera, manager, floor, and synthetic source are
preserved across rebuilds.

## Profiling notes

- Open `Window → Analysis → Profiler`, enable GPU profiling, capture at least
  60 stable frames after a few seconds of warmup.
- The synthetic source animates pan/tilt by default to exercise the Rodrigues
  rotation path each frame. Disable `animatePanTilt` on the source component
  to measure static-fixture cost in isolation.
- The two camera variants stress the URP path differently:
  - **InsideCones** — worst case for the URP fullscreen lighting pass since
    many cones overlap per pixel.
  - **OutsideCones** — best case; only a few cones in frame.
- For URP volumetric resolution / step-count sweeps, edit those fields on the
  spawned `VRSL URP Light Manager` GameObject between captures.

## What is NOT covered

- Build & upload smoke testing — that's a manual VRChat client check, not a
  profiling exercise.
- AudioLink path profiling — this sample targets the DMX path. The AudioLink
  manager has the same architectural shape, so DMX results generalise.
