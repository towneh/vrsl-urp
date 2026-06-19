# Changelog

## [Unreleased]

### Added

- `albedoTintStrength` slider on `VRSL_URPLightManager` and `VRSL_AudioLinkURPLightManager`. Modulates each light's surface contribution by a pre-light scene-colour snapshot as an albedo proxy: `0` is pure additive (the previously shipped behaviour — light added on top of the surface unmodulated, can read as washed-out under bright spots), `1` is fully multiplicative (light picks up the surface's hue and dark surfaces stay dark, closer to physical reflectance). Default is `0` so existing scenes are unchanged. When non-zero the lighting pass records an extra fullscreen blit that captures the active camera colour into a private RT (`_VRSLOpaqueTexture`); the cost (~0.1 ms desktop, more under SPSI VR) is skipped entirely at 0. URP's own `_CameraOpaqueTexture` isn't used because under URP 17 render graph mode `CopyColor` doesn't reliably run for this injection point.

- `DMXFixtureType.StaticPointLight` — a static fixture archetype on `VRStageLighting_DMX_RealtimeLight` that emits as an omnidirectional point light. The manager forces point-light mode for the type and the inspector hides the spot, cone, pan/tilt, gobo, and output-axis fields, so authoring a light bar or par as a point source is a one-pick preset rather than a manual toggle. Runtime behaviour matches a static par with `isPointLight` enabled — the DMX path already carried the point/spot flag end to end, so the type adds authoring ergonomics, not new rendering.
- `use5ChannelMode` on `VRStageLighting_DMX_RealtimeLight` — decodes DMX using the compressed 5-channel static layout (dimmer +0, RGB +1/2/3, strobe +4) instead of the standard 13-channel layout (dimmer +5, RGB +7/8/9, strobe +6), matching the fixture-body surface shader's `_5CH_MODE`. The render-pass light previously only decoded the 13-channel layout, so fixtures patched 5 channels apart cross-talked — each light read its neighbours' channels. The flag rides a reserved slot of the per-fixture GPU config, so there is no buffer-stride change; gobo/spin reads (+10/+11) are suppressed in 5CH since those channels belong to the next fixture.

- `VRSL_URPLightManager` now publishes its assigned DMX grid CRTs as the `_VRSLU_DMX*` shader globals that fixture-body surface shaders sample, so the manager alone drives both the render-pass lights and the fixture-body emissive — `VRSL_LocalUIControlPanel` is no longer required to set those globals for the URP path. A new `dmxStrobeTimerTexture` slot supplies the StrobeTimings CRT (`_VRSLU_DMXGridStrobeTimer`) that the StrobeOutput CRT's decode shader needs to compute the strobe gate. The manager also forces its assigned CustomRenderTextures into Realtime update mode, so it fully replaces `VRSL_LocalUIControlPanel` for the URP path.
- The DMX light manager now derives a fixture's light origin from the centre of the fixture-body mesh it drives (`fixtureShellRenderers[0].bounds.center`) when no `lensTransform` is assigned, before falling back to the component's own transform. This places the light at the lit geometry even when the fixture component sits away from it — e.g. fixtures parked at a shared root while their bar sub-meshes are spread across the scene (common in imported environments where the bars share one transform, so both the component transform and a `lensTransform` pointed at the bar resolve to the root).

### Fixed

- DMX **point-light** fixtures (`isPointLight` / `StaticPointLight`) were silently masked to near-zero whenever the manager had gobo textures assigned. The compute clamped every fixture's gobo to slot 0, and `SampleGobo` then projected it as a spot cone — returning 0 for the spill behind the light axis and multiplying the front by the (often dark) slot-0 texture. Point lights now keep `goboIdx = -1` (the `SampleGobo` no-op), so their omnidirectional spill is unmasked and scales with intensity as expected. Symptom was a large decoded intensity with no visible scene spill that didn't respond to `maxIntensity`.
- The URP `VRSL_LocalUIControlPanel` custom inspector aborted every repaint with a `FileNotFoundException` because `GetVersion()` read a `Runtime/VERSION.txt` the package never shipped. The throw happened before `base.OnInspectorGUI()`, so none of the panel's serialized fields drew — the CRT source arrays (`DMX_CRTS_Horizontal` etc.) looked empty and couldn't be wired, leaving the `_VRSLU_*` DMX globals unpublished and fixture-body surfaces unlit. `GetVersion()` now guards the read with `File.Exists` and a try/catch fallback, and the package ships `Runtime/VERSION.txt`.

### Known issues

- **Par, blinder, laser, and discoball fixture meshes do not render correctly under single-pass instanced VR.** The mesh body / projection geometry on these fixture types is missing or misplaced in one or both eyes. Moving heads (spots and washes) and the surface lighting and volumetric-cone passes are unaffected. A previous fix (`1746a30`) addressed the same class of issue for the moving-head body / volumetric meshes by routing their shaders through `#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON` and making their `DepthOnly` / `DepthNormals` passes SPSI-aware; the par, blinder, laser, and discoball shaders still need the equivalent treatment.

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
