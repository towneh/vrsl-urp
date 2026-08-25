# VRSL URP Fixture Configuration Guide

Setup and authoring for VRSL realtime lights on Unity 6 URP. For installation and a first-run walkthrough, see the README. For pipeline architecture, struct layouts, and tuning internals, see `URP-Realtime-Volumetric-Lights.md`.

This guide covers two paths:

- **DMX** — fixtures driven by an Artnet/OSC signal through the existing CRT chain.
- **AudioLink** — fixtures driven by AudioLink's audio analysis texture, no DMX needed.

The two paths share most authoring concepts; differences are called out per section.

---

## Quickstart

The three URP-only shaders (`Hidden/VRSL-URP/DeferredLighting`, `Hidden/VRSL-URP/VolumetricLighting`, `Hidden/VRSL-URP/SurfaceProperties`) ship at `Runtime/Shaders/Surface/` inside this package. URP is a hard dependency, so the shaders compile unconditionally and the Light Manager menu utilities can resolve them via `Shader.Find` without any sample-import step.

VRSL never reads, mutates, or recommends URP asset / URP renderer asset settings — those belong to your project. The realtime light path is implemented entirely through runtime pass injection and a VRSL-owned surface prepass, so it co-exists with whatever URP renderer configuration the project uses.

Two menu utilities under `VRSL → URP` cover scene-level setup. Both are idempotent — safe to re-run.

| Menu | Effect |
|---|---|
| **VRSL → URP → Add Light Manager to Active Scene** | Creates a `VRSL URP Light Manager` GameObject in the active scene with the compute, light-cull, lighting, surface-properties and volumetric shader references assigned. |
| **VRSL → URP → AudioLink Config → Setup AudioLink Realtime Lights in Scene** | Adds `VRStageLighting_AudioLink_RealtimeLight` to every AudioLink mover spotlight in the active scene and wires up pan/tilt transforms. |

The managers inject their render passes at runtime via `RenderPipelineManager.beginCameraRendering`, so there is no `ScriptableRendererFeature` to add to the URP Renderer asset. This is what lets the package work in environments where users don't author the renderer asset (notably VRChat worlds, where the renderer is owned by the VRChat client).

`VRSLSurfacePrepass` (enqueued automatically by the manager) captures the surface data the lighting pass shades against. Authored normals come from the same `DepthNormals` / `DepthNormalsOnly` shader tags URP's built-in depth-normals prepass uses, so any opaque shader with a URP `DepthNormals` pass (URP Lit / Poiyomi URP / lilToon URP / Mochie URP) contributes automatically; surfaces drawn by shaders without one fall back to depth-derivative normals. Albedo, smoothness and metallic come from a second draw using the `SurfaceProperties` shader as an override, which keeps each renderer's own material values — that is what lets a lit surface keep its texture colour. Third-party shader authors don't need to add anything VRSL-specific for either.

The remainder of this document describes the per-fixture fields exposed in the inspectors.

---

## Manager Setup

### DMX — `VRSL_URPLightManager`

| Field | Asset |
|---|---|
| `dmxMainTexture` | CRT producing `_VRSLU_DMXGridRenderTexture` |
| `dmxMovementTexture` | CRT producing `_VRSLU_DMXGridRenderTextureMovement` |
| `dmxStrobeTexture` | CRT producing `_VRSLU_DMXGridStrobeOutput` |
| `dmxStrobeTimerTexture` | StrobeTimings CRT, published as `_VRSLU_DMXGridStrobeTimer`. The StrobeOutput CRT samples it to compute the strobe gate; leave empty if strobe is unused. |
| `dmxSpinTimerTexture` | CRT producing `_VRSLU_DMXGridSpinTimer` |
| `computeShader` | `VRSLDMXLightUpdate` |
| `lightCullShader` | `VRSLLightCull`. Builds the per-tile light list. Leave empty to disable tiled culling. |
| `lightingShader` | `Hidden/VRSL-URP/DeferredLighting` |
| `surfacePropertiesShader` | `Hidden/VRSL-URP/SurfaceProperties`. Drives the albedo / smoothness / metallic prepass. Leave empty to light every surface as a neutral mid-grey dielectric. |
| `volumetricShader` | `Hidden/VRSL-URP/VolumetricLighting` |
| `goboTextures` | Optional `Texture2D[]` packed into a shared `Texture2DArray`; DMX channel +11 selects the slot. |

On enable the manager publishes its assigned DMX CRTs as the `_VRSLU_DMX*` shader globals that fixture-body surface shaders sample, so the manager alone drives both the render-pass lights and the fixture-body emissive — no separate control panel is needed to set those globals. It also forces each assigned `CustomRenderTexture` into Realtime update mode, so the decode chain keeps producing live data on its own.

### AudioLink — `VRSL_AudioLinkURPLightManager`

| Field | Asset |
|---|---|
| `computeShader` | `VRSLAudioLinkLightUpdate` |
| `lightCullShader` | `VRSLLightCull`. Builds the per-tile light list. Leave empty to disable tiled culling. |
| `lightingShader` | `Hidden/VRSL-URP/DeferredLighting` |
| `surfacePropertiesShader` | `Hidden/VRSL-URP/SurfaceProperties`. Drives the albedo / smoothness / metallic prepass. Leave empty to light every surface as a neutral mid-grey dielectric. |
| `volumetricShader` | `Hidden/VRSL-URP/VolumetricLighting` |
| `goboTextures` | Optional `Texture2D[]` for the shared gobo wheel. |
| `samplingTexture` | Optional `Texture2D` or `RenderTexture` sampled by every fixture in `ColorTexture` / `ColorTextureTraditional` color modes. Per-fixture `textureSamplingCoordinates` UVs pick the colour. Leave empty when no fixtures use those modes. |

The AudioLink manager auto-discovers `_AudioTexture` from the global shader property; no texture references are needed.

### Volumetric tuning (both managers)

The volumetric pass runs whenever `volumetricShader` is assigned. Inspector fields:

| Field | Effect |
|---|---|
| `volumetricStepCount` | Integration steps per light, spent across the part of the ray inside that light's own cone (default 24). Cost scales with steps × lights per tile. Because the span is bounded to the cone rather than to the geometry behind it, low counts go further than they would otherwise — 16 holds up on 60° spots at 20 m range. Wide cones, long beams and dense haze want more. |
| `volumetricDensity` | Base scattering density. |
| `volumetricAnisotropy` | Henyey–Greenstein g (default 0.2; 0 = isotropic; positive forward-scatters). |
| `volumetricTint` / `volumetricIntensity` | Colour tint and global multiplier. |
| `volumetricUseNoise` + scale / scroll / strength | Modulated 3D-noise density. Off compiles the noise out (zero cost). |
| `coupleToSceneFog` | Multiply density by `unity_FogParams.x` and tint by `unity_FogColor` so a URP VolumeProfile drives shaft brightness globally. |
| `volumetricResolution` | `Half` (default; live VR) or `Full` (cinematic capture; ~4× per-pixel cost). |

To disable the volumetric cones at runtime without touching the shader assignment, drop `volumetricIntensity` to 0.

---

## Fixture Authoring — Shared Fields

These fields appear on both `VRStageLighting_DMX_RealtimeLight` and `VRStageLighting_AudioLink_RealtimeLight`:

| Field | Notes |
|---|---|
| `fixtureType` | `MoverSpotlight`, `MoverWashlight`, `StaticBlinder`, `StaticParLight`, `StaticPointLight`, `Custom`. Drives inspector field visibility and sets the wash-vs-spot inner-cone ratio (wash 0.65 = flat-bright with long feather; spot/static 0.5 = falloff over the outer half). `StaticPointLight` emits omnidirectionally — the manager forces point mode for it and the inspector hides the spot, cone, pan/tilt, and gobo fields. |
| `maxIntensity` | Output at full DMX / full amplitude, on the same scale as a URP spot light's **Intensity** value. A fixture at full output with Final and Global Intensity at 1 matches a URP spot set to the same number, so you can calibrate against a reference light instead of by eye. |
| `range` | Attenuation range in metres. |
| `spotAngle` (AudioLink) / `minSpotAngle` & `maxSpotAngle` (DMX) | Outer cone angle in degrees. DMX channel +4 lerps between min and max. |
| `isPointLight` | Emit as a point light instead of a spot. |
| `emitterDepth` | (m) Pushes the cone apex back along the light direction so the cone arrives at the lens with finite radius `emitterDepth × tan(halfAngle)`. Default 0 reproduces a point source. As a starting point: an LED-bar fixture with a 30° outer half-angle reads well at `emitterDepth ≈ 0.3–0.5 m`. |
| `lensTransform` | Optional child `Transform`. When assigned, the cone's apex projects from the lens position instead of the prefab root — useful on hung mover prefabs where the prefab root sits at the truss-clamp. URP DMX and AudioLink mover prefabs ship with a baked `LensTransform` child; leave empty for fixtures whose root already sits at the lens. |
| `fixtureShellRenderers` | Optional `MeshRenderer[]` on the fixture body. The Realtime light pushes a `MaterialPropertyBlock` so each renderer's lit-lens emissive picks up the same DMX or AudioLink data. |
| `goboIndex` | Selects a slot in the manager's shared gobo wheel. AudioLink uses 1-based indexing (1 = open beam, 2–8 = shaped gobos). |
| `goboSpinSpeed` | Bipolar rotation speed for the projected gobo (negative = CCW, positive = CW). |

### Pan/tilt (movers only)

| Field | Notes |
|---|---|
| `enablePanTilt` | Enable for moving-head fixtures. |
| `panTransform` | Transform rotated on Y for pan. Its world position becomes the light origin. |
| `tiltTransform` | Transform rotated on X for tilt. Its world forward becomes the light direction. |
| `maxMinPan` / `maxMinTilt` | Total travel in degrees (±half from centre). |
| `panOffset` / `tiltOffset` | Per-fixture aim offset. |
| `invertPan` / `invertTilt` | Reverse direction. |

DMX movers get pan/tilt from the DMX channels and apply Rodrigues rotation on the GPU; the `panTransform` / `tiltTransform` references are unused on the DMX path. AudioLink movers read pan/tilt from the animated transforms each frame, so you must wire your animator to those transforms.

---

## DMX-Specific Authoring

`VRStageLighting_DMX_RealtimeLight`:

| Field | Notes |
|---|---|
| `enableDMXChannels` | Enable DMX control. |
| `dmxChannel` / `dmxUniverse` | Industry-standard channel and Artnet universe (1-based). |
| `useLegacySectorMode` / `sector` | Legacy sector addressing for older patches. Sector 0 = channels 1–13, sector 1 = 14–26, etc. |
| `enableFineChannels` | 16-bit pan/tilt via the +1 / +3 channels. |
| `use5ChannelMode` | Read DMX using the compressed 5-channel static layout (intensity +0, RGB +1/2/3, strobe +4) instead of the standard 13-channel layout. For fixtures patched 5 channels apart. Match this to the channel mode of the fixture-body surface shader so surface and light read the same data. |
| `enableStrobe` | Allow the DMX strobe channel to gate the light on/off. |
| `enableConeWidth` | Allow ch+4 (motor speed / zoom) to modulate the cone between `minSpotAngle` and `maxSpotAngle`. Disable on par cans and blinders so unrelated traffic on ch+4 doesn't flicker their cone width. |
| `enableGobo` | Allow ch+11 to select gobos. |
| `enableGoboSpin` | Allow ch+10 to drive gobo spin speed. |
| `finalIntensity` / `globalIntensity` | User-side intensity caps (0–1). |

The full per-fixture channel layout (offsets relative to `dmxChannel`):

| Offset | Channel |
|---|---|
| +0 | Pan coarse |
| +1 | Pan fine |
| +2 | Tilt coarse |
| +3 | Tilt fine |
| +4 | Motor speed / cone width |
| +5 | Dimmer / intensity |
| +6 | Strobe gate |
| +7 / +8 / +9 | Red / Green / Blue |
| +10 | Gobo spin speed |
| +11 | Gobo selection |

With `use5ChannelMode` enabled this collapses to the 5-channel static form — dimmer at +0, Red / Green / Blue at +1 / +2 / +3, strobe at +4 — and the motor, pan/tilt, and gobo channels are unused. Patch those fixtures 5 channels apart and set the fixture-body surface shader to its matching 5-channel mode.

---

## AudioLink-Specific Authoring

`VRStageLighting_AudioLink_RealtimeLight`:

| Field | Notes |
|---|---|
| `enableAudioLink` | When enabled, intensity is driven by AudioLink amplitude. When disabled, the light runs at full `maxIntensity × finalIntensity` regardless — useful for static fixtures in the same scene. |
| `band` | `Bass` / `LowMids` / `HighMids` / `Treble`. |
| `delay` | History delay (0 = most recent, 127 = most delayed). Useful for chasing effects across a row of fixtures. |
| `bandMultiplier` | Sensitivity multiplier — increase if amplitude reads too low for your audio levels. |
| `colorMode` | `Emission` (fixed `emissionColor`), `ThemeColor0–3`, `ColorChord`, `ColorTexture` (modern, HSV-normalised), `ColorTextureTraditional` (raw sample). |
| `emissionColor` | Active when `colorMode == Emission`. Author in HDR for bright fixtures. |
| `textureSamplingCoordinates` | Active in `ColorTexture` modes — UV into `_AudioTexture` to read the colour from. |
| `targetToFollow` | Optional aim target. When set, the fixture's tilt transform tracks this object. |
| `finalIntensity` / `globalIntensity` | User-side intensity caps. |

### Tuning maxIntensity

AudioLink amplitude is normalised (0–1), so `maxIntensity` directly controls peak output. Calibration:

1. Play a loud section so the target band is near full amplitude.
2. Adjust `maxIntensity` until illumination on a lit surface matches your artistic intent.
3. Use `bandMultiplier` to control sensitivity (how quickly the light reaches `maxIntensity` from quiet audio) independently of peak brightness.

---

## Sibling-Static Inheritance

For projects that retain the legacy `VRStageLighting_DMX_Static` / `VRStageLighting_AudioLink_Static` components on the same GameObject (typically VRChat scenes targeting both the volumetric mesh shaders and the URP path), the Realtime light components automatically inherit configuration from a sibling Static, so authors edit one component and both pipelines stay in sync.

| Realtime field | Sibling source (DMX) | Sibling source (AudioLink) |
|---|---|---|
| Addressing | sector / channel / universe / fine-channel mode | — |
| Pan/tilt modifiers | invertPan, invertTilt, maxMinPan, maxMinTilt, panOffset, tiltOffset | — |
| AudioLink reaction | — | enableAudioLink, band, delay, bandMultiplier |
| Final intensity | — | finalIntensity |
| Emission | — | emissionColor (`LightColorTint` on Static) |
| Gobo | — | goboIndex (`SelectGOBO`), goboSpinSpeed (`SpinSpeed`) |

Inherited fields render as read-only "(inherited)" widgets in the Realtime light inspector so the effective values stay visible at a glance.

The supplied URP prefab variants ship without a Static sibling — the Realtime light is the sole authoring surface and drives the fixture body emissive directly via `fixtureShellRenderers`. Sibling-Static inheritance only applies when one is explicitly added.

---

## Writing a fixture shader: depth passes

Three things, if you are authoring a shader rather than configuring a project.

1. **A shader that draws in the opaque queue range needs a `DepthOnly` pass and a
   `DepthNormals` one.** Under depth priming URP draws opaque geometry with an `Equal`
   depth test against a prepass, so a shader that wrote no depth there, or wrote
   different depth, is dropped from the frame entirely. Both passes, because URP runs
   one prepass or the other depending on whether anything in the frame has asked it for
   a normals texture — screen-space ambient occlusion and screen-space decals both
   do — and priming tests against whichever one ran. A shader carrying both is correct
   in either project.
2. **Whatever the forward vertex stage does, the depth passes must do too.** Pan and
   tilt rotation, cone width and range scaling, alpha clipping, and the collapse to
   degenerate geometry that hides a fixture at zero intensity all count. The package's
   own moving-head shaders declare their depth passes with the same defines, structs
   and include chain as their forward pass for exactly this reason — copy that shape.
3. **No scene light is needed for depth.** It comes from the pipeline.

`VRSL → URP → Validate Renderer Setup` checks the open scene against all of this and
names anything that would not draw.

## Runtime API

```csharp
// DMX: after changing a fixture's config field
VRSL_URPLightManager.Instance.MarkConfigDirty();

// DMX or AudioLink: after adding or removing fixtures at runtime
VRSL_URPLightManager.Instance.RefreshFixtures();
VRSL_AudioLinkURPLightManager.Instance.RefreshFixtures();
```

AudioLink's per-frame fields (`band`, `delay`, `bandMultiplier`, `colorMode`, `emissionColor`, `maxIntensity`, `finalIntensity`, `enableAudioLink`, `goboIndex`, `goboSpinSpeed`) are read every `LateUpdate` and don't require a refresh call.

---

## Prefabs

URP prefab variants ship under `Runtime/Prefabs/`:

| Path | Contents |
|---|---|
| `Runtime/Prefabs/AudioLink/AudioLink-URP-Fixtures/` | AudioLink mover spotlight, mover washlight, static blinder, static parlight, plus the manager prefab |
| `Runtime/Prefabs/DMX/Horizontal Mode/DMX-13CH-URP-Fixtures/` | DMX 13-channel fixtures + manager (horizontal patch layout) |
| `Runtime/Prefabs/DMX/Vertical Mode/DMX-13CH-URP-Fixtures/` | Same fixtures, vertical patch layout |
| `Runtime/Prefabs/DMX/Legacy Mode/DMX-13CH-URP-Fixtures/` | Manager prefab for legacy sector-mode patches |
| `Runtime/Prefabs/DMX/*/5-Channel Statics/DMX-5CH-URP-Fixtures/` | DMX 5-channel static fixtures (par, blinder) |

URP prefabs are standalone — they ship without the legacy `*_Static` sibling component and without the volumetric mesh GameObject. The Realtime light component is the sole authoring surface, and the volumetric pass renders the cone.

---

## Example Scenes

| Scene | Path |
|---|---|
| `VRSL-ExampleScene-AudioLink-URPRealtimeLights` | `Runtime/Example Scenes/AudioLink-Scenes/` |
| `VRSL-ExampleScene-EditorViaOSC-Horizontal-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |
| `VRSL-ExampleScene-EditorViaOSC-Vertical-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |

Each ships with the manager pre-populated.
