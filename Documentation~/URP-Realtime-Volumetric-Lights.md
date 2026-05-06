# VRSL URP Realtime & Volumetric Lights — Architecture

VRSL's URP path drives genuine scene illumination from the same fixture data the volumetric beams already use. Surface lighting and the visible cone are layered into one render-graph pipeline reading a single GPU-resident light buffer.

This document is the architecture and tuning reference. For setup steps and per-fixture authoring, see `URP-Fixture-Configuration-Guide.md`.

---

## Requirements

| | Minimum |
|---|---|
| Unity | 6.0 LTS |
| Universal Render Pipeline | 17.0 |
| AudioLink (AudioLink path only) | installed and active in the scene |

The pipeline lives in the `Towneh.VRSL.URP` assembly, which targets URP ≥17.0 (Unity 6) unconditionally — URP is a hard dependency of this package. The DMX CRT decode chain is shared with the volumetric mesh shaders; AudioLink scenes can run without GridReader or any DMX source.

The two managers (`VRSL_URPLightManager` for DMX, `VRSL_AudioLinkURPLightManager` for AudioLink) inject their render passes at runtime by subscribing to `RenderPipelineManager.beginCameraRendering` and calling `EnqueuePass` directly on each camera's `ScriptableRenderer`. There is no `ScriptableRendererFeature` to add to the URP Renderer asset — the package works without any renderer-asset authoring step.

Authored surface normals come through a VRSL-owned prepass (`VRSLNormalsPrepass`) that renders opaque scene geometry into a non-MSAA RT bound to `_VRSLNormalsTexture`, using the same `DepthNormals` / `DepthNormalsOnly` shader tags URP's built-in depth-normals prepass uses. Any opaque shader that ships a URP-compatible `DepthNormals` pass — URP Lit, Poiyomi URP, lilToon URP, Mochie URP — contributes its authored normals automatically; third-party shader authors don't need to add anything VRSL-specific. Pixels drawn by shaders without a URP `DepthNormals` pass fall back to a depth-derivative normal reconstruction in the lighting shader, so those surfaces still pick up VRSL light, just faceted to the underlying tessellation. The prepass RT is allocated as a `Tex2DArray` with `volumeDepth` matching the camera target so the per-eye normals are correct under Single-Pass Stereo Instanced VR.

---

## Pipeline Overview

```
Per-fixture config (StructuredBuffer)
        │
        ▼ [BeforeRenderingOpaques]
[COMPUTE PASS — VRSLDMXLightUpdate.compute or VRSLAudioLinkLightUpdate.compute]
  Decodes per-fixture state into VRSLLightData (GPU-resident, 80 bytes/light)
        │
                │
        ▼ [AfterRenderingPrePasses]
[NORMALS PREPASS — VRSLNormalsPrepass]
  Renders opaque geometry with the DepthNormals / DepthNormalsOnly
  shader tags into a VRSL-owned non-MSAA Tex2DArray bound globally as
  _VRSLNormalsTexture. Independent of URP's _CameraNormalsTexture, so
  the lighting pass works under any MSAA setting on the URP asset.
        │
        ▼ [AfterRenderingOpaques]
[SURFACE — VRSLDeferredLighting.shader]
  Fullscreen additive triangle. Reconstructs world position from depth,
  reads the surface normal from _VRSLNormalsTexture (with a depth-
  derivative fallback for pixels drawn by shaders without a URP
  DepthNormals pass), evaluates every fixture per pixel, accumulates
  onto the colour target.
        │
        ▼ [AfterRenderingOpaques, immediately after surface lighting]
[VOLUMETRIC — VRSLVolumetricLighting.shader]
  Half-res raymarched in-scattering by default; full-res additive available.
  Reads the same VRSLLightData buffer the surface pass produces; cones are
  screen-space depth-occluded against on-screen geometry.
```

Both data sources (DMX, AudioLink) write the same `VRSLLightData` struct, so the surface and volumetric shaders are identical between paths. The pass classes (`ComputePass`, `LightingPass`, `VolumetricPass`) live as nested types inside the static container classes `VRSLDMXLightPasses` and `VRSLAudioLinkLightPasses`. The manager instantiates them and enqueues them per camera; there is no `ScriptableRendererFeature` involved.

### Differences between data sources

| Aspect | DMX (`VRSL_URPLightManager`) | AudioLink (`VRSL_AudioLinkURPLightManager`) |
|---|---|---|
| Intensity / colour | DMX dimmer, RGB, strobe channels in the CRT chain | AudioLink band amplitude × bandMultiplier; theme / chord / texture colour |
| Direction | Pan/tilt channels decoded on the GPU via Rodrigues rotation | `tiltTransform.forward` read on the CPU each frame |
| Config upload | Once at setup; re-uploaded only when `MarkConfigDirty()` is called | Every frame in `LateUpdate` (animated transforms change continuously) |
| Strobe | Dedicated channel (pre-baked binary gate from `_VRSLU_DMXGridStrobeOutput`) | Not applicable |
| Fine channels | Optional 16-bit pan/tilt via +1 / +3 | Not applicable |

The AudioLink CPU cost per frame is `N × 112 bytes` uploaded as one `GraphicsBuffer.SetData` call — well within typical SetData latency for any practical fixture count. There is no GPU→CPU readback in either path; the AudioLink and DMX textures stay GPU-resident.

---

## GPU Data Structs

All struct fields use `float4` rather than `float3` to guarantee identical layout between C# `[StructLayout(Sequential)]` and HLSL `StructuredBuffer` across all platforms.

### VRSLFixtureConfig (DMX) — 128 bytes, 8 × float4

| Field | Contents |
|---|---|
| `positionAndRange` | xyz = world position, w = attenuation range |
| `forwardAndType` | xyz = base forward direction, w = light type (0 = spot, 1 = point) |
| `rightAndMaxIntensity` | xyz = local +X in world space (tilt rotation axis), w = max intensity |
| `spotAngles` | x = inner-to-outer ratio (0..1), y = max outer half-angle (deg), z = finalIntensity × globalIntensity, w = min outer half-angle (deg) |
| `dmxChannel` | x = absolute channel, y = enableStrobe, z = enablePanTilt, w = enableFineChannels |
| `panSettings` | x = maxMinPan (deg), y = panOffset (deg), z = invertPan (0/1), w = enableGoboSpin (0/1) |
| `tiltSettings` | x = maxMinTilt (deg), y = tiltOffset (deg), z = invertTilt (0/1), w = enableGobo (0/1) |
| `extras` | x = emitterDepth (m), yzw = reserved |

### VRSLALFixtureConfig (AudioLink) — 112 bytes, 7 × float4

| Field | Contents |
|---|---|
| `positionAndRange` | xyz = world position (per-frame), w = attenuation range |
| `forwardAndType` | xyz = world forward (per-frame from `tiltTransform.forward`), w = light type |
| `intensityParams` | x = maxIntensity, y = finalIntensity × globalIntensity, z = AudioLink active flag (1 = sample, 0 = static full), w = unused |
| `spotAngles` | x = inner-to-outer ratio (0..1), y = outer half-angle (deg), z = emitterDepth (m), w = unused |
| `alParams` | x = band (0–3), y = delay (0–127), z = bandMultiplier, w = colorMode (0–7) |
| `emissionColor` | xyz = linear RGB (used when colorMode == 0), w = unused |
| `reserved` | x = gobo array index (-1 = none), y = gobo spin speed, zw = textureSamplingCoordinates UV (used when colorMode == 6 or 7) |

### VRSLLightData — 80 bytes, 5 × float4 (shared)

Compute pass output, surface and volumetric pass input.

| Field | Contents |
|---|---|
| `positionAndRange` | xyz = world position, w = range |
| `directionAndType` | xyz = normalised direction, w = type |
| `colorAndIntensity` | xyz = linear RGB, w = combined intensity |
| `spotCosines` | x = cos(innerHalfAngle), y = cos(outerHalfAngle), z = active flag (0 = skip), w = emitterDepth (m) |
| `goboAndSpin` | x = gobo array index (-1 = none, 0+ = `_VRSLGobos` slice), y = pre-integrated spin angle (radians, fmod 2π) |

### colorMode values (AudioLink)

| Value | Source |
|---|---|
| 0 | Fixed `emissionColor.rgb` |
| 1–4 | Theme Color 0–3 (`_AudioTexture` x = colorIndex, y = 23) |
| 5 | Color Chord representative (`_AudioTexture` x = 0, y = 25) |
| 6 | ColorTexture (modern) — sampled at `textureSamplingCoordinates`, HSV-normalised so value = 1 |
| 7 | ColorTexture (traditional) — raw sample, no normalisation |

---

## Lighting Library

`Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl` provides the shared evaluation functions called by both surface and volumetric passes:

```hlsl
float VRSL_DistanceAttenuation(float distSq, float range)
{
    float d2 = distSq / (range * range);
    float f  = saturate(1.0 - d2 * d2);
    return (f * f) / max(distSq, 0.0001);   // smoothed inverse-square
}

float VRSL_SpotAttenuation(float3 lightDir, float3 toLight, float cosInner,
                           float cosOuter, float emitterDepth)
{
    // emitterDepth pushes the cone apex back along lightDir so the cone has
    // finite radius at the light position; 0 = point source.
    float3 toApex = toLight - lightDir * emitterDepth;
    float cosAngle = dot(-lightDir, normalize(toApex));
    float t = saturate((cosAngle - cosOuter) / max(cosInner - cosOuter, 0.0001));

    // Confine contribution to the lens-forward hemisphere with a 5cm soft
    // transition. Without this, an apex pushback would let the cone illuminate
    // geometry between the virtual apex and the lens (including the inside of
    // the fixture body, which then bleeds through the outer mesh — this
    // pipeline doesn't cast shadows).
    float forwardOfLens = dot(-toLight, lightDir);
    float lensClip      = smoothstep(0.0, 0.05, forwardOfLens);

    return t * t * lensClip;
}
```

`SampleGobo(goboIdx, spinAngle, posWS, lightPos, lightDir, cosOuter, emitterDepth)` derives light-space right/up from `lightDir`, projects the world point to light-space UV using `tan(halfAngle)` derived from the stored cosine, and samples `_VRSLGobos` (a `Texture2DArray` of all gobo slots packed at scene init). The UV projection uses the same virtual apex as `VRSL_SpotAttenuation` (`lightPos - lightDir × emitterDepth`) so the gobo mask tracks the widened cone instead of clamping back to the original cone radius. Spin phase is applied in radians directly to the UV — no `_Time` multiplication in the shader.

Henyey–Greenstein phase function (volumetric only):

```hlsl
float VRSL_HenyeyGreenstein(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / (4.0 * VRSL_PI * pow(max(denom, 0.0001), 1.5));
}
```

---

## Compute Pass Details

### DMX — `VRSLDMXLightUpdate.compute`

One thread per fixture, 64 threads/group. Per fixture:

1. Read `VRSLFixtureConfig` from the StructuredBuffer.
2. Sample DMX channels — `GetDMXValue(absChannel + offset, _DMXTex)` ports the legacy `getValueAtCoords()` function exactly, including the empirical UV offsets (`-0.015`, `-0.001915`) and the 13th-channel correction table for ranges 90–101, 160–205, 326–404, 676–819, ≥1339.
3. If `enablePanTilt`, decode pan and tilt (coarse + optional fine) and apply Rodrigues rotation: tilt around the fixture's world-space local +X axis first, then pan around the base forward.
4. Cone width — `outerHalf = lerp(spotAngles.w, spotAngles.y, ch+4)`, `innerHalf = outerHalf × spotAngles.x` (the inner-to-outer ratio). Tracks dynamic cone-width zoom while preserving the inner cone's character.
5. Sample the SpinnerTimer CRT for accumulated gobo spin phase.
6. Write `VRSLLightData` with `spotCosines.w = emitterDepth` (from `extras.x`) and `colorAndIntensity.w` set to 0 if intensity is negligible (active flag tells the surface pass to skip).

### AudioLink — `VRSLAudioLinkLightUpdate.compute`

Same shape, but direction comes pre-supplied in the config (no Rodrigues), and the data source is `_AudioTexture`. Integer `Load()` is used rather than bilinear sampling because each AudioLink texel encodes discrete data. Color sampling honours `colorMode`:

- Theme rows at `y = 23` (modes 1–4)
- Color chord at `(0, 25)` (mode 5)
- ColorTexture (mode 6) samples `_AudioTexture` at `textureSamplingCoordinates`, converts RGB → HSV, sets V = 1, converts back so any non-black pixel emits at full brightness
- ColorTexture (mode 7) returns the raw sampled pixel

Gobo spin is integrated on the GPU each frame: `spinPhase = fmod(spinSpeed × _VRSLTime × -π/9, 2π)`. The negative sign and π/9 factor match the volumetric mesh shader's stripe-pattern rotation rate; `fmod` keeps the phase bounded so `sin`/`cos` in the surface pass don't lose precision over long sessions.

---

## Surface Lighting Pass

`VRSLDeferredLighting.shader`, full-screen triangle, `Blend One One` over the active colour target.

- Three vertices generated entirely from `SV_VertexID` — no vertex buffer.
- World position reconstructed via `ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP)`.
- Surface normal sampled from `_VRSLNormalsTexture` (written by `VRSLNormalsPrepass` at `AfterRenderingPrePasses`). On pixels where the prepass wrote no normal — surfaces drawn by shaders without a URP `DepthNormals` pass — the shader falls back to `normalize(cross(ddy(posWS), ddx(posWS)))` so those surfaces still pick up VRSL light, just faceted to the underlying tessellation rather than smooth-shaded.
- Per-pixel loop over `_VRSLLights[0..N-1]`, skipping inactive lights, accumulating `colour × intensity × distAtten × spotAtten × NdotL` plus the per-pixel gobo sample.

`ConfigureInput(ScriptableRenderPassInput.Depth)` runs in the manager's per-camera callback before enqueue. The lighting pass declares `_CameraDepthTexture` as a tracked Render Graph resource and samples `_VRSLNormalsTexture` through the global binding the prepass sets up via `SetGlobalTextureAfterPass`.

### Albedo tint

`albedoTintStrength` on the manager (range 0–1, default 0) modulates the accumulated additive contribution by the pre-light scene colour as an albedo proxy: the shader applies `lighting *= lerp(1, sceneColor, _VRSLAlbedoTint)` immediately before the additive blend. At `0` the pass is purely additive and the scene-colour sample is skipped entirely. At `1` the surface response is fully multiplicative — light picks up the surface's hue and dark surfaces stay dark, which reads as physically more correct in ambient-dominated stage scenes but loses contribution on near-black surfaces. Intermediate values blend between the two.

When the strength is non-zero, the lighting pass records an extra **opaque-capture** sub-pass first: a fullscreen blit (Pass 1 of `VRSLDeferredLighting.shader`) that copies the active camera colour into a transient render-graph texture sized to the camera target (Tex2DArray when SPSI VR is active, matching the camera's `volumeDepth`). The captured texture is bound globally as `_VRSLOpaqueTexture` and consumed by the lighting sub-pass. URP's own `_CameraOpaqueTexture` isn't used: under URP 17 render graph mode `CopyColor` doesn't reliably run for our injection point, even when "Opaque Texture" is enabled on the URP asset and `ConfigureInput(Color)` is requested, so the package captures its own snapshot. The capture sub-pass is skipped entirely when `albedoTintStrength == 0`, so the feature is genuinely opt-in cost-wise (~0.1 ms desktop, more under SPSI VR).

True per-pixel albedo isn't available in this lightweight prepass (only normals are written); the camera-colour snapshot is the closest stand-in URP exposes universally. It includes any other lighting (URP realtime / baked / ambient) that has already affected the surface, so the proxy is most accurate when ambient dominates and least accurate in heavily realtime-lit scenes — tune to taste rather than expecting physical correctness.

---

## Volumetric Pass

`VRSLVolumetricLighting.shader`. Runs whenever the `volumetricShader` field is assigned on the manager.

### Resolution modes

`volumetricResolution` on the manager selects:

- **Half** (default) — three sub-passes:
  - Pass 0: depth downsample. Min-depth filter on each 2×2 source quad keeps the half-res depth tight to silhouettes.
  - Pass 1: half-res jittered raymarch into an `R16G16B16A16_SFloat` half-res RT.
  - Pass 2: 9-tap Gaussian-weighted bilateral upsample, additive over the camera colour. The bilateral term `1 / (eps + |fullEye - halfEye|)` rejects taps across silhouettes.
- **Full** — single pass at the camera target resolution; samples `_CameraDepthTexture` directly and additive-blends. ~4× per-pixel cost vs Half but no upsample artefacts.

The half-res raymarch jitters the ray origin per pixel using an R2 (plastic-constant) low-discrepancy sequence with frame-indexed offset, so head and fixture motion average the residual pattern over time.

### Density model

`multi_compile _ _VRSL_VOLUMETRIC_NOISE` toggles between two variants:

- **Off** (clean) — uniform `volumetricDensity` per step.
- **On** (modulated) — density is multiplied by a procedural hash-based 3D value noise sampled in world space and drifting vertically on `_Time.y`. ~50 ALU per step (~5–10% extra raymarch cost at typical fixture counts). The volumetric pass sets the keyword on the volumetric material each frame from the manager flag, so disabling fully removes the noise code from the active variant.

### Scene-fog coupling

`coupleToSceneFog` (off by default). When on:

```hlsl
density *= max(unity_FogParams.x, 0.0);
tint    *= unity_FogColor.rgb;
```

A URP VolumeProfile then drives shaft brightness and tint globally — turn fog up, beams brighten; turn fog off, beams suppress.

### Occlusion

Screen-space only. The raymarch terminates at `_CameraDepthTexture` per pixel, so on-screen geometry, avatars, and props correctly silhouette out of the cone. Off-screen occluders (an avatar in the beam viewed from the side) do not cast a darkened wedge through the rest of the volume — that requires a per-fixture shadowmap, deliberately deferred for cost reasons (see *Known Limitations*).

### Per-fixture emitter depth

`emitterDepth` pushes the conceptual cone apex back along `lightDir` by that distance. The cone arrives at the fixture lens with finite radius `emitterDepth × tan(halfAngle)` instead of converging to a point, matching the visible beam to wide-aperture fixtures (LED bars, par cans). Passes through to both surface and volumetric attenuation via `VRSLLightData.spotCosines.w`, and `SampleGobo` projects from the same virtual apex so the gobo mask follows the widened cone.

The inspector exposes the field with a `Range` of `[0, 1.0]`. `VRSL_SpotAttenuation` clamps contribution to the lens-forward hemisphere with a 5cm soft transition, so an apex pushback never lights surfaces behind the lens (including the inside of the fixture body).

### Manager parameter packing

```hlsl
float4 _VRSLVolStepCount;   // x = step count, y = fog coupling flag, w = HG anisotropy g
float4 _VRSLVolDensity;     // x = base density, y = noise scale, z = noise scroll, w = noise strength
float4 _VRSLVolFogTint;     // xyz = tint, w = global intensity multiplier
```

Uploaded once per frame as global vectors in the volumetric pass `SetRenderFunc`.

---

## Render Graph Integration

All passes use the Unity 6 Render Graph API (`RecordRenderGraph`). Resources are imported into the graph as `BufferHandle` / `TextureHandle` with explicit `AccessFlags`, letting URP insert the correct GPU memory barriers and validate dependencies at compile time.

A few Unity 6-specific requirements are worth flagging for contributors:

- Raster passes that call `cmd.SetGlobalBuffer` / `cmd.SetGlobalInteger` / `cmd.SetGlobalTexture` must declare `builder.AllowGlobalStateModification(true)` before `SetRenderFunc`, or Unity throws `InvalidOperationException: Modifying global state from this command buffer is not allowed`.
- `ConfigureInput` is called on each pass instance before `EnqueuePass` — for the runtime-injection path this happens inside the manager's `beginCameraRendering` callback per camera. URP reads the flags during enqueue and schedules the depth-texture copy and depth-normals prepass automatically.
- `Texture2DArray` resources (the gobo wheel) are bound via `Shader.SetGlobalTexture` in the manager's per-camera callback rather than inside the render graph itself, since the graph only accepts `TextureHandle`.

---

## Performance Model

- **Per-frame CPU cost is bounded.** DMX uploads the config once at setup and on `MarkConfigDirty()`; AudioLink uploads `N × 112 bytes` per frame. `VRStageLighting_DMX_RealtimeLight.OnValidate` raises a static `ConfigChanged` event the DMX manager subscribes to, so inspector tweaks propagate to the GPU on the next `LateUpdate` without authors needing to call `MarkConfigDirty` themselves. No per-fixture CPU decode; no `Light` component writes; no `MaterialPropertyBlock` push per cone.
- **No GPU→CPU readback** in either path.
- **No shadow pass penalty.** Bypassing Unity's `Light` component means URP doesn't generate per-light shadow atlases — the architectural choice that makes 100+ fixtures feasible. URP's per-light shadow atlas at scale is the dominant cost in the equivalent `Light`-component approach (one full scene redraw per shadow-casting spot, six per point light).
- **GPU cost is dominated by the fullscreen passes.** The compute dispatch is one workgroup per 64 fixtures and finishes in well under 1 ms at any practical fixture count. Scaling to large rigs is a GPU-bandwidth conversation, not CPU.

---

## Known Limitations

- **No shadow casting.** This pipeline bypasses Unity's `Light` component to avoid the per-light shadow atlas cost. A future opt-in `castVolumetricShadows` flag could drive a small pool of real `Light` components for hero fixtures.
- **No light-perspective shadows in volume.** On-screen occluders silhouette out of the cone correctly; off-screen occluders (an avatar in the beam viewed from the side) don't cast a darkened wedge through the rest of the volume.
- **Transparent geometry is not illuminated.** The additive surface and volumetric passes run after opaques; haze, glass, and water materials don't receive contribution. The legacy volumetric mesh shaders remain available alongside for haze-only effects on platforms that need them.
- **NineUniverse DMX mode not supported.** The compute shader implements the standard `IndustryRead` sampling path only.
- **DMX and AudioLink simultaneously on the same camera write the same `_VRSLLights` global** — the last-scheduled feature wins. A unified-buffer extension is future work.

---

## File Reference

| File | Assembly | Purpose |
|---|---|---|
| `Runtime/Scripts/VRStageLighting_DMX_RealtimeLight.cs` | Towneh.VRSL.URP | DMX per-fixture config component |
| `Runtime/Scripts/VRStageLighting_AudioLink_RealtimeLight.cs` | Towneh.VRSL.URP | AudioLink per-fixture config component |
| `Runtime/Scripts/VRSL_URPLightManager.cs` | Towneh.VRSL.URP | DMX manager singleton |
| `Runtime/Scripts/VRSL_AudioLinkURPLightManager.cs` | Towneh.VRSL.URP | AudioLink manager singleton |
| `Runtime/Scripts/VRSLDMXLightPasses.cs` | Towneh.VRSL.URP | DMX pass classes (compute + surface + volumetric) |
| `Runtime/Scripts/VRSLAudioLinkLightPasses.cs` | Towneh.VRSL.URP | AudioLink pass classes |
| `Editor/VRSL_URPRendererSetup.cs` | Towneh.VRSL.URP.Editor | Read-only renderer-config diagnostics and the scene-level "Add Light Manager" menu (DMX) |
| `Runtime/Shaders/Compute/VRSLDMXLightUpdate.compute` | — | DMX compute kernel |
| `Runtime/Shaders/Compute/VRSLAudioLinkLightUpdate.compute` | — | AudioLink compute kernel |
| `Runtime/Shaders/Surface/VRSLDeferredLighting.shader` | — | Fullscreen surface lighting pass (shared) |
| `Runtime/Shaders/Surface/VRSLVolumetricLighting.shader` | — | Raymarched volumetric pass (shared) |
| `Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl` | — | Struct definitions and lighting evaluation functions |
| `Editor/VRStageLighting_DMX_RealtimeLightEditor.cs` | Towneh.VRSL.URP.Editor | DMX custom inspector |
| `Editor/VRStageLighting_AudioLink_RealtimeLightEditor.cs` | Towneh.VRSL.URP.Editor | AudioLink custom inspector |
| `Editor/VRSL_AudioLinkURPSetup.cs` | Towneh.VRSL.URP.Editor | Scene-wide AudioLink fixture configuration utility |
| `Editor/VRSL_EditorHeader.cs` | Towneh.VRSL.URP.Editor | Shared logo + version-bar helper |
| `Editor/VRSL_URPProfilingSampleMenu.cs` | Towneh.VRSL.URP.Editor | `VRSL → Profiling → Import Profiling Sample` menu entry |
| `Samples~/RealtimeLightProfiling/` | Towneh.VRSL.URP.Profiling, Towneh.VRSL.URP.Profiling.Editor | Opt-in sample: scene builder window and synthetic CRT-bypass DMX source for benchmarking the realtime light path |
