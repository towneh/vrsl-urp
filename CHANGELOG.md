# Changelog

## [Unreleased]

### Added

- `albedoTintStrength` slider on `VRSL_URPLightManager` and `VRSL_AudioLinkURPLightManager`. Modulates each light's surface contribution by a pre-light scene-colour snapshot as an albedo proxy: `0` is pure additive (the previously shipped behaviour — light added on top of the surface unmodulated, can read as washed-out under bright spots), `1` is fully multiplicative (light picks up the surface's hue and dark surfaces stay dark, closer to physical reflectance). Default is `0` so existing scenes are unchanged. When non-zero the lighting pass records an extra fullscreen blit that captures the active camera colour into a private RT (`_VRSLOpaqueTexture`); the cost (~0.1 ms desktop, more under SPSI VR) is skipped entirely at 0. URP's own `_CameraOpaqueTexture` isn't used because under URP 17 render graph mode `CopyColor` doesn't reliably run for this injection point.

## [0.1.0] — Initial release

### URP Realtime Lights — Unity 6 / URP 17+

The pipeline is four Render Graph passes — all reading the same per-fixture GPU buffer — injected at runtime by the manager MonoBehaviour via `RenderPipelineManager.beginCameraRendering`. No URP Renderer Features required, no URP asset / renderer asset settings touched by the package; co-exists with whatever rendering path, MSAA setting, depth-priming mode, and depth-texture configuration the project uses.

A VRSL-owned normals prepass renders opaque scene geometry with the standard URP `DepthNormals` / `DepthNormalsOnly` shader tags into a VRSL-owned RT, so authored surface normals from any URP-targeted shader (URP Lit, Poiyomi URP, lilToon URP, Mochie URP) come through automatically — avatars and props receive smooth-shaded VRSL light without their authors needing to add anything URP-specific.

- **Compute** decodes per-fixture state (position, direction, colour, intensity, cone, gobo) into a `StructuredBuffer`.
- **DMX** — `VRStageLighting_DMX_RealtimeLight` + `VRSL_URPLightManager`. Decodes the existing CRT chain on the GPU; no per-frame CPU cost per fixture.
- **AudioLink** — `VRStageLighting_AudioLink_RealtimeLight` + `VRSL_AudioLinkURPLightManager`. Reads animated transform directions on the CPU each frame and samples the global `_AudioTexture` on the GPU. Per-fixture color modes include emission, theme colors, ColorChord, and ColorTexture sampling against an optional scene-wide `samplingTexture` on the manager.

URP fixture prefab variants ship as standalone — the Realtime light is the sole authoring surface and drives the fixture body emissive directly via `fixtureShellRenderers`. Volumetric controls (resolution mode, modulated 3D-noise density, scene-fog coupling) and per-fixture controls (`emitterDepth`, `globalIntensity`, `targetToFollow`, `lensTransform`) are exposed on the manager and Realtime light inspectors. See `Documentation~/Realtime-Volumetric-Lights.md` and `Documentation~/Fixture-Configuration-Guide.md` for details.

Editor menu utilities:
- **VRSL → URP → Add Light Manager to Active Scene** drops a configured manager into the active scene with compute / lighting / volumetric shader references assigned.
- **VRSL → URP → Setup AudioLink Realtime Lights in Scene** configures every AudioLink mover spotlight in one click.

### Package origin

Extracted from the `urp-volumetric-lights` development branch on towneh's fork of `com.acchosen.vr-stage-lighting`, then refactored as a coexisting URP overlay package so the URP path can ship and evolve independently of the upstream VRChat-targeted package.

### Coexistence with com.acchosen.vr-stage-lighting

Hard dependency on `com.acchosen.vr-stage-lighting >= 2.8.1`. This package consumes upstream's CRT DMX decode pipeline (`_Udon_DMXGridRenderTexture*` globals), AudioLink subscription, and the legacy mesh-shader fixture geometry referenced by the URP fixture prefabs. Existing upstream scenes and prefabs continue to work alongside; this package only adds new URP fixtures and rendering, never replaces upstream assets.
