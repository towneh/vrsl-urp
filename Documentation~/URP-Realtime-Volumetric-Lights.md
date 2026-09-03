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

Surface data comes through a VRSL-owned prepass (`VRSLSurfacePrepass`) that renders opaque scene geometry into non-MSAA RTs, twice at most:

- **Normals**, using the same `DepthNormals` / `DepthNormalsOnly` shader tags URP's built-in depth-normals prepass uses, into `_VRSLNormalsTexture`. Any opaque shader that ships a URP-compatible `DepthNormals` pass contributes its authored normals automatically. Pixels drawn by shaders without one fall back to a depth-derivative normal reconstruction in the lighting shader, so those surfaces still pick up VRSL light, just faceted to the underlying tessellation. **Where URP's own depth-normals prepass can be read, this draw does not run**: the manager asks URP for normals and publishes `_CameraNormalsTexture` under the same global name, so the lighting shader never learns which it got. See *Where the normals come from* below.
- **Albedo, smoothness and metallic**, using `VRSLSurfaceProperties` as a `DrawingSettings.overrideShader` over the opaque forward tags, into `_VRSLAlbedoTexture` (rgb = base colour, a = smoothness) and `_VRSLMaterialTexture` (r = metallic). An override shader keeps each renderer's own material property values, so this reaches albedo on shaders VRSL knows nothing about. See *Material capture* below for how the two property-naming conventions are resolved.

Neither half asks third-party shader authors to add anything VRSL-specific. Both RTs are allocated as `Tex2DArray` with `volumeDepth` matching the camera target so per-eye data is correct under Single-Pass Stereo Instanced VR.

Both draws honour **Lit surfaces** (`prepassLayers`) on the manager, a layer mask defaulting to everything. Geometry on a layer left out is not drawn into either target and lights as the neutral mid-grey dielectric with a depth-derived normal: still lit, but without its own colour, gloss or normal map. `VRSL → URP → Validate Renderer Setup` reports the mask beside the renderer's own and names what it leaves out. The prepass is not enqueued at all while the manager has no fixtures, since nothing would read its targets.

### Where the normals come from

`VRSLPrepassPolicy` decides per camera, on the CPU, before the passes are enqueued. URP's `_CameraNormalsTexture` holds exactly what VRSL's normals draw would produce: the same shader passes, the same `R8G8B8A8_SNorm` format, world space, measured bit-identical over every written pixel (row S13). So wherever URP can draw it, the manager requests `ScriptableRenderPassInput.Normal`, which turns URP's depth prepass into a depth-normals one, and VRSL skips its own draw. That is one opaque geometry pass fewer per camera on a renderer with depth priming on, where the depth prepass was running anyway.

Where URP cannot draw it, VRSL draws its own as before, into its own single-sample target. URP never multisamples its normals texture, but under depth priming its prepass draws into the camera's depth attachment, and on a multisampled camera that puts a single-sample colour beside a multisampled depth: the frame is a Render Graph error and nothing renders, not a wrong picture. Stock URP declines to prime on a multisampled camera; a project's own URP may not, and the package cannot tell which it is running on. The policy therefore reads URP's normals only when

- the camera renders at MSAA 1, or its renderer has depth priming `Disabled`;
- the renderer is Forward or Forward+ (Deferred packs its normals differently);
- `prepassLayers` is Everything, because URP's prepass draws every layer and a surface left out of VRSL's is meant to light with a depth-derived normal;
- `forceOwnNormals` on the manager is off.

The camera's sample count is predicted the way URP computes it: a camera with a target texture takes the texture's, one without takes the pipeline asset's. Anything URP goes on to lower it by makes that an over-estimate, which errs towards drawing VRSL's own normals.

`forceOwnNormals` (under *Troubleshooting*) draws VRSL's own normals everywhere. It exists for a URP version where the texture's contents turn out to differ, and as the comparison switch for row S11. `Validate Renderer Setup` says for each camera which source it will use and why; `VRSL Diagnostics` on a manager says which the last camera got.

The two halves can't be merged into one geometry pass: a shader-tag draw renders each material's own pass (which is what supplies authored normal maps) and an override draw replaces it (which is what supplies albedo). Skipping the albedo half by leaving `surfacePropertiesShader` unassigned is supported and drops the cost back to one pass, at the price of every surface lighting as a neutral mid-grey dielectric.

---

## Pipeline Overview

```
Per-fixture config (StructuredBuffer)
        │
        ▼ [BeforeRenderingOpaques]
[COMPUTE PASS — VRSLDMXLightUpdate.compute or VRSLAudioLinkLightUpdate.compute]
  Decodes per-fixture state into VRSLLightData (GPU-resident, 64 bytes/light)
        │
                │
        ▼ [AfterRenderingPrePasses]
[SURFACE PREPASS — VRSLSurfacePrepass]
  Two opaque geometry draws into VRSL-owned non-MSAA Tex2DArrays:
  authored normals via the DepthNormals / DepthNormalsOnly shader tags
  (_VRSLNormalsTexture), and albedo / smoothness / metallic via
  VRSLSurfaceProperties as an override shader (_VRSLAlbedoTexture,
  _VRSLMaterialTexture). Where URP's own depth-normals prepass can be
  read (VRSLPrepassPolicy), its texture is published as _VRSLNormalsTexture
  instead and the normals draw is skipped.
        │
        ▼ [BeforeRenderingOpaques + 1]
[TILE CULL — VRSLLightCull.compute]
  One thread group per 16x16 screen tile per eye. Tests each active
  light's bounding sphere against the tile frustum and writes the
  survivors into a per-tile index list.
        │
        ▼ [AfterRenderingOpaques]
[SURFACE — VRSLDeferredLighting.shader]
  Fullscreen additive triangle. Reconstructs world position from depth,
  reads the surface normal from _VRSLNormalsTexture (with a depth-
  derivative fallback for pixels drawn by shaders without a URP
  DepthNormals pass) and the material inputs from the prepass, then
  evaluates the tile's lights through URP's BRDF onto the colour target.
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

The DMX column describes the CRT chain. With a channel source assigned (see
[`DMX-Channel-Sources.md`](DMX-Channel-Sources.md)) the same values arrive as bytes in a GPU
channel buffer and the chain is bypassed; the Basis integration is one such source.

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
| `extras` | x = emitterDepth (m), y = use5ChannelMode (0/1), zw = reserved |

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

### VRSLLightData — 64 bytes, 4 × float4 (shared)

Compute pass output, surface and volumetric pass input.

| Field | Contents |
|---|---|
| `positionAndRange` | xyz = world position, w = range |
| `directionAndType` | xyz = normalised direction, w = type |
| `colorAndIntensity` | xyz = linear RGB, w = combined intensity |
| `spotParams` | x = cos(innerHalfAngle), y = cos(outerHalfAngle), z = emitterDepth (m), w = pre-integrated gobo spin angle (radians, fmod 2π) |

Two values ride in packed slots rather than taking rows of their own, so the struct fits
four `float4` instead of five. Read them through the accessors in
`VRSLLightingLibrary.hlsl` rather than by field:

| Accessor | Source |
|---|---|
| `VRSL_LightType(light)` | `directionAndType.w`, low bit — 0 = spot, 1 = point |
| `VRSL_GoboIndex(light)` | `directionAndType.w` above the low bit, biased by one so -1 (no gobo) survives |
| `VRSL_IsActive(light)` | `colorAndIntensity.w > 0` — a fixture emitting nothing is written with zero intensity, so no separate flag is needed |

Both packed values are small integers, which floats represent exactly, so the packing is
lossless. Packing them as halves instead would have quantised the spin phase and stippled
a slowly rotating gobo.

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
6. Write `VRSLLightData` with `spotParams.z = emitterDepth` (from `extras.x`), the light type and gobo slice packed into `directionAndType.w`, and `colorAndIntensity.w` set to 0 when the fixture emits nothing so the readers skip it.

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
- Surface normal sampled from `_VRSLNormalsTexture` (written by `VRSLSurfacePrepass` at `AfterRenderingPrePasses`). On pixels where the prepass wrote no normal, which is any surface drawn by a shader with no URP `DepthNormals` pass, the shader falls back to `normalize(cross(ddy(posWS), ddx(posWS)))` so those surfaces still pick up VRSL light, just faceted to the underlying tessellation rather than smooth-shaded.
- Per-pixel loop over the tile's light list (see *Tiled light culling*), evaluating each through URP's BRDF.

`ConfigureInput` runs in the manager's per-camera callback before enqueue: `Depth` always, and `Normal` as well where `VRSLPrepassPolicy` reads URP's normals for that camera. The lighting pass declares `_CameraDepthTexture` as a tracked Render Graph resource and samples the prepass targets through the global bindings the prepass sets up via `SetGlobalTextureAfterPass`.

### Surface response

Material inputs are read once per pixel and fed to URP's own BRDF, so a VRSL fixture shades a Lit material the way a URP spot light would:

```hlsl
InitializeBRDFData(albedo, metallic, 0, smoothness, alpha, brdfData);
...
float3 radiance = lightColour * lightIntensity * (distAtten * spotAtten * NdotL);
return DirectBRDF(brdfData, normalWS, lightDirWS, viewDirWS) * radiance;
```

That gives the diffuse albedo term (a lit red carpet stays red instead of washing towards white), the GGX specular lobe (a stage spot on a polished floor gets its highlight), and the metallic response (metals lose their diffuse and tint their specular).

Where the prepass wrote nothing, whether that is geometry drawn by a shader with no forward LightMode tag or a scene running without `surfacePropertiesShader` assigned, the shader falls back to a mid-grey dielectric so those surfaces still respond to light. The lighting pass pushes `_VRSLSurfaceDataValid` so that case is explicit rather than depending on what an unbound texture slot resolves to, which differs by graphics API.

Shadowing is still absent, so a fixture lights every surface in range regardless of occluders. See *Known Limitations*.

### Intensity scale

`maxIntensity` is on the same scale as a URP spot light's **Intensity** value in
non-physical mode. The surface pass evaluates

```
radiance = colour * intensity * distanceAttenuation * NdotL
result   = DirectBRDF(...) * radiance
```

which is the same form URP's `LightingPhysicallyBased` uses, against distance and cone
attenuation functions written to match URP's. So a VRSL fixture at full output with Final
and Global Intensity at 1 matches a URP spot set to the same number, and authors can
calibrate against a reference light rather than by eye.

For that to hold, the DMX dimmer curve has to reach exactly 1 at full. The curve exists so
the cast light ramps together with the fixture-body glow, whose emission goes as
`dimmer² × (1 + (curveMod − 1)·dimmer)`; the compute divides by `curveMod` so the shape is
preserved while the peak lands at 1. The AudioLink path is linear in amplitude and needs no
correction, so both sources now agree at full output.

### Material capture

`VRSLSurfaceProperties` declares both property-naming conventions, `_BaseMap` / `_BaseColor` (URP-native) and `_MainTex` / `_Color` (the legacy naming most avatar shaders use), and combines them with `min()`:

- a URP material leaves the legacy pair at its default white, so the URP pair wins;
- an avatar material leaves the URP pair at white, so the legacy pair wins;
- a material converted from Standard that still carries a stale `_MainTex` pointing at the same texture resolves to that texture once instead of squaring it.

Where a material genuinely populates both with different values the darker wins, which under-applies light rather than blowing it out. A tint whose alpha reads zero is treated as a property the material doesn't declare, since an unbound scalar resolves to zero rather than to the shader's stated default and a transparent tint is meaningless on an opaque renderer.

Smoothness takes `max(_Smoothness, _Glossiness)` on the same reasoning. Metallic and smoothness maps aren't sampled — the scalars only.

An override shader replaces the material's shader outright, which is what reaches albedo on shaders VRSL knows nothing about, but it also means any visibility decision made *inside* that shader never runs here. Poiyomi's UV Tile Discard is the clearest case: in its default Vertex mode it returns NaN from the vertex program, collapsing the triangle in the passes that feed `_CameraDepthTexture`, while the prepass, running VRSL's shader rather than Poiyomi's, draws the geometry as though it were there. The lighting pass would then take position from the camera's depth (the surface behind) and albedo from the prepass (the hidden avatar), and the discarded shape would reappear as lit colour on whatever stood behind it.

The prepass therefore publishes its depth as `_VRSLSurfaceDepthTexture`, and `VRSL_SurfaceDataCovers` in `VRSLSurfaceBRDF.hlsl` drops the albedo read wherever that disagrees with the camera's depth, falling through to the neutral dielectric. The comparison is in linear eye space against a tolerance proportional to viewing distance: raw depth precision is far from uniform, and the two values come from different shader compilations of the same transform, so they agree closely rather than bit-exactly. Custom alpha clips and vertex displacement produce the same mismatch and are covered by the same check — a displaced material lights as neutral grey rather than as a ghost offset from the mesh.

The opaque and alpha-test queues are drawn as separate renderer lists against separate passes, so an opaque material whose base map stores non-colour data in alpha is never clipped against a stale `_Cutoff`.

### Contact shadows

`contactShadowStrength` on the manager (0 disables, and the trace compiles out) marches the
depth buffer from each lit pixel towards each fixture. Where the march finds geometry in the
way, the light's contribution is scaled down.

Positions are projected with `ComputeNormalizedDeviceCoordinatesWithZ`, the exact inverse of
the `ComputeWorldSpacePosition` call that reconstructed the pixel, so the trace can't drift
against the depth buffer it samples. A per-pixel interleaved-gradient dither offsets the
first step so the fixed step size reads as grain rather than banding.

What it does and doesn't cover matters:

- **Covers** near-field occlusion the camera can see — an avatar in a beam shadowing the
  floor at its feet, a prop shadowing the surface it stands on.
- **Doesn't cover** anything off screen, or beyond the level's trace distance. A wall between a
  fixture and a surface across the room casts nothing, and an occluder just outside the
  frame stops shadowing as it leaves. Those need a light-perspective shadow map.

It is the most expensive term in the lighting loop, a depth march per light per pixel, so
it runs last, only for lights still reaching the pixel after attenuation and the gobo, and
is **off by default**. `High` is the setting for thin occluders and for shadows that
disappear at grazing angles: it traces further and samples more finely than `Standard`.
If distant background bleeds shadow forward, that is the trace reaching past what it
should and `Standard` is the shorter one.

`contactShadowStrength` is the only control: 0 is off, 1 is fully shadowed where
occluded. Trace length, sample count and how thick a depth-buffer surface is treated as
being all come from the quality level, because each of them sets frame time and none can
be judged by eye. A strength of 0, and any strength at `Off`, zeroes the packed step
count, which the shader reads as skip, rather than tracing and scaling the result to
nothing.

### Tiled light culling

`VRSLLightCull.compute` runs one thread group per 16×16 screen tile per eye. Each group builds its tile's world-space frustum from the same inverse view-projection the fullscreen shaders reconstruct with, tests every active light's bounding sphere against it, and writes the survivors into `_VRSLTileLightIndices` — one run of `MaxLightsPerTile + 1` uints per tile, 257 at the current cap, the first holding the count.

Both fullscreen passes then iterate the tile's list rather than the whole fixture buffer. The volumetric pass gains the most: a view ray stays inside its screen tile for its whole length, so the list is resolved once and serves every light that pixel marches, rather than each pixel walking every fixture in the scene.

Tile frusta span the camera's full depth range rather than each tile's scene-depth bounds. That costs a little tightness on the surface pass, but keeps one list valid for the volumetric ray (which runs from the camera to the surface) and removes any dependency on the depth texture being ready when the cull runs.

The per-tile cap is 256 fixtures; past that, fixtures are dropped for that tile rather than falling back to the full list. The cull records the count a tile asked for rather than the count it drew, so the diagnostics can report how much light a dense rig is losing, since a tile at its limit and a tile far past it would otherwise read identically. Nothing may iterate slot 0 of the tile list directly for that reason; `VRSL_LightListCount` clamps, and there are no indices behind the count past the cap. Leaving `lightCullShader` unassigned publishes a zero `_VRSLTileParams`, which both shaders read as "iterate every light": correct, just without the saving.

---

## Volumetric Pass

`VRSLVolumetricLighting.shader`. Runs whenever the `volumetricShader` field is assigned on the manager.

### Per-light march span

Each light in the tile's list is integrated over its own span of the view ray rather than all of them sharing one march from the camera to the opaque surface. The span is built in two stages:

1. Intersect the ray with the light's bounding sphere (`positionAndRange`), clamped to `[0, distance-to-surface]`. A light behind the camera or fully occluded drops out here, before any stepping.
2. For spots, narrow that span to the cone itself via `VRSL_NarrowSpanToCone`. Point lights fill their sphere, so they keep the stage-1 span.

Both stages matter, and for different reasons.

Stage 1 decouples step size from scene depth. A shared march divides the step budget across the whole ray, so its step size is set by whatever geometry sits behind the beam rather than by the beam. A cone a few metres away with a wall thirty metres behind it would be crossed in one or two steps.

Stage 2 is what makes the sample budget actually land in the light. A sphere is a poor proxy for a cone: at 20 m range the chord runs to 40 m, it contains the entire backward hemisphere, and everything outside the beam angle. A ray crossing a beam near the lens covers well under a metre of lit space, so without this the great majority of steps sample dark and the few inside carry the whole result. That is large per-pixel quadrature error, and the jitter cannot hide error of that size — it only reshapes it into whatever pattern the dither itself carries, which is how it surfaces visually. The effect is worst near a fixture head, where the cone is narrowest relative to its sphere.

`VRSL_NarrowSpanToCone` relies on a cone nappe being convex, so a ray meets it in exactly one interval. The quadratic supplies the surface crossings; midpoint tests select which sub-interval is inside. That avoids case analysis on root signs and puts the ray-parallel-to-surface degeneracy on the same path as the general case. It projects from the same virtual apex as `VRSL_SpotAttenuation`, so the marched span matches the cone the attenuation lights.

### Step count

The number of steps a light is marched with follows the length of its span:

```hlsl
int steps = clamp((int)ceil(span / spacing), 4, maxSteps);
```

`spacing` and `maxSteps` come from the quality level, so what governs sample density is metres of cone per step rather than a count. A cone clipping half a metre of the ray takes the floor of four; one running thirty metres down it takes the ceiling. The floor is load-bearing: one or two samples across a short span alias into dots that swim as the camera moves, which is worse than the cost they would save.

Two consequences are the intent rather than faults. The samples a pixel takes depend on what is in front of it rather than on the light count alone, so frame time follows content more than a fixed count would. And a long span at `High` may take more steps than the same span at `Standard`: the level sets density, and the maximum is a ceiling rather than a target.

Worst-case cost is `maxSteps` steps per light per pixel, as it would be for a shared march. It improves in the common case: a ray missing the sphere costs one intersection test, a ray missing the cone costs the quadratic plus three midpoint tests, and a ray crossing a short span pays for the span rather than for the budget.

### Visibility bound

Before a light is stepped, the march bounds the most it can add across the whole span and skips it when that cannot be seen. The bound is evaluated at the ray's closest approach to the light within the span, where distance attenuation peaks. The angular falloff, the gobo, the phase function and the density noise can each only reduce a sample from there, so every step is at or below that peak and the sum over the span is at or below peak times span. The phase term is the closed-form maximum of Henyey-Greenstein at the current anisotropy, and the colour term is the brightest channel rather than luminance, so an all-blue light is not bounded by how little blue weighs.

The test is conservative by construction: it marches lights it need not and never skips one that would have shown. What it removes is the case where a ray grazes the far tail of a fixture's falloff and would otherwise pay the whole march to accumulate almost nothing.

The threshold, `VRSL_VOL_MIN_CONTRIB` in the volumetric shader, is in the units the pixel is written in: linear radiance after density, span, tint and the global intensity have been applied. That is what lands in the frame, so it holds whatever range a rig's decoded intensities run to. 1/4096 is under one 8-bit step at black. It is a compile-time constant and zero switches the test off without touching anything else.

Two consequences worth knowing:

- Density noise (`_VRSL_VOLUMETRIC_NOISE`) is sampled per light rather than once per shared step, so each beam's haze follows its own sample positions. Where cones overlap that is one texture fetch per light per step.
- Each light's step size differs, and the accumulation weights by that light's own `stepSize`, so overlapping cones still sum to the same result as marching them together.

Because the span is tight, every step lands inside the beam and the budget buys far more than it would against an untargeted march. Wide cones, long beams and dense haze are the cases `High` is for; a narrow spot needs very few steps at either level.

### Quality levels

`quality` on either manager is the one cost control, and its values are constants in
`VRSLQuality.cs` rather than serialised fields. Both managers read the same table, so a
scene carrying both light paths cannot march at two budgets depending on which manager
owns the pass.

| Level | Beams | Sample spacing | Max steps per light | Noise | Contact shadows | Steps | Distance | Thickness |
|---|---|---|---|---|---|---|---|---|
| `Off` | no | — | — | — | no | — | — | — |
| `Low` (mirrors only) | yes | 0.70 m | 12 | yes | no | — | — | — |
| `Standard` | yes | 0.35 m | 24 | yes | yes | 8 | 1.5 m | 0.5 m |
| `High` | yes | 0.20 m | 40 | yes | yes | 16 | 2.5 m | 0.35 m |

`Standard` is the default and reproduces what the package shipped before the level
existed, so a scene authored earlier renders and costs what it did. `Off` records no
volumetric pass and allocates no volumetric targets, rather than recording one that draws
nothing. `Low` is not offered for a scene: it is what a secondary camera renders at under
the `Reduced` policy when the scene is at `Standard` (see *Camera Selection*), and the
inspector leaves it out of the list.

The march is half-resolution and only half-resolution, in three sub-passes:

- Pass 0: depth downsample. Min-depth filter on each 2×2 source quad keeps the half-res depth tight to silhouettes.
- Pass 1: half-res jittered raymarch into an `R16G16B16A16_SFloat` half-res RT.
- Pass 2: 9-tap bilateral upsample, additive over the camera colour. Taps are weighted by a separable tent centred on the pixel's true sub-texel position within the half-res grid, times a depth term of `1 / (1 + d²)` where `d` is the eye-depth difference over a tolerance proportional to viewing distance.

The upsample is bilateral rather than trilinear, which is what makes half-resolution
affordable instead of a compromise: it rejects taps across a depth discontinuity and so
holds an edge a trilinear filter would smear. `High` spends its extra budget on step
count for that reason: against a bilateral upsample, a finer march buys more per unit
cost than more pixels would.

Two details of the upsample are load-bearing rather than incidental:

- **Weights follow the sub-texel position, not the nearest texel centre.** Snapping would give all four full-res pixels of a 2×2 block identical taps and identical weights, making the composite constant across the block. A constant-per-block reconstruction of a smooth gradient terraces into contours, and a beam is almost entirely smooth gradient. Tent radius is 1.5 so a tap has reached zero weight by the time the centre texel flips, which keeps the reconstruction continuous across texel boundaries.
- **The depth term needs a real tolerance, not a divide guard.** Weighting by `1/depthDiff` alone is unbounded as the difference approaches zero, which gives the centre tap a weight orders of magnitude above its neighbours on any surface that isn't exactly fronto-parallel and collapses the kernel to a point sample.

The raymarch offsets each pixel's step phase with interleaved gradient noise. The dither has to decorrelate in both screen axes: a Weyl lattice, `frac(a*x + b*y)`, has straight diagonal iso-value contours, so every pixel along one diagonal receives the same phase and residual stepping streaks rather than breaking up. An offset from `_VRSLVolTime` decorrelates across frames so motion averages the residual out.

Note that a dither only conceals quadrature error while that error is small. If stepping is visible as a *structured* pattern rather than fine grain, the sample budget is landing in the wrong place — check the march span before reaching for the dither or the step count.

A suspected upsample artefact is told from a march artefact by reading the half-resolution target in a frame capture: an artefact present there is in the march, one that appears only in the composite is in passes 0 or 2.

### Density model

`multi_compile _ _VRSL_VOLUMETRIC_NOISE` toggles between two variants:

- **Off** (clean) — uniform `volumetricDensity` per step.
- **On** (modulated) — density is multiplied by a 3D value noise sampled in world space and drifting vertically on `_VRSLVolTime`, the clock the manager passes in as `VolumetricTime`. The volumetric pass sets the keyword on the volumetric material each frame from the manager flag, so disabling fully removes the noise code from the active variant.

The raymarch never reads `_Time.y`. Its dither phase and the haze scroll both run on `_VRSLVolTime`, which the manager fills with seconds since level load, so every camera in a frame sees one value and a capture that has to be repeatable can hold it through the manager's `VolumetricTimeOverride`. The test rig's freeze does exactly that: at the step floor of four the dither reaches the quantised output on a few grazing pixels, and two captures at different frames would otherwise differ by one 8-bit step on them.

The field is a texture, not a function evaluated per sample. Each manager bakes it once, on the first frame that needs it, with the `BakeVolumetricNoise` kernel both light-update computes carry: 64 texels per axis of `R8_UNorm`, 256 KB, over a lattice of 16 cells, so four texels span each cell and the sampler's linear filter follows the smoothstep between lattice points rather than flattening it. The kernel evaluates `VRSL_ValueNoise3DPeriodic` from the lighting library, the same value noise as `VRSL_ValueNoise3D` on a lattice that wraps, so the texture cannot drift from the function that defines it and the period is an implementation detail rather than an import setting. The march then takes one repeat-wrapped trilinear fetch per light per step where the procedural form cost eight hash taps.

The field repeats every 16 lattice units, which at the shipped noise scale of 0.3 is every 53 m of world. A density modulation has no feature a viewer can recognise at a second sighting, so the repeat is not readable.

A compute without the kernel gets a single white texel and one warning: the field reads 1 everywhere, so a beam loses its haze and keeps everything else, rather than dimming to a third against an unbound texture reading 0.

### Diagnostics

The raymarch keeps four counters per pixel: pixels marched, lights marched, steps taken and lights skipped by the visibility bound. Collecting them is one atomic per counter per pixel, so it is a request rather than a switch: `VRSL Diagnostics` arms the probe on its first call and reports steps per light, lights marched per pixel and the share of lights skipped on the next, and a sweep row carries the same three figures beside its timings, collected after each timed window so the atomics never land inside a measured frame. The step count and the bound are both designed to leave the image alone, which is why these exist.

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

`emitterDepth` pushes the conceptual cone apex back along `lightDir` by that distance. The cone arrives at the fixture lens with finite radius `emitterDepth × tan(halfAngle)` instead of converging to a point, matching the visible beam to wide-aperture fixtures (LED bars, par cans). Passes through to both surface and volumetric attenuation via `VRSLLightData.spotParams.z`, and `SampleGobo` projects from the same virtual apex so the gobo mask follows the widened cone.

The inspector exposes the field with a `Range` of `[0, 1.0]`. `VRSL_SpotAttenuation` clamps contribution to the lens-forward hemisphere with a 5cm soft transition, so an apex pushback never lights surfaces behind the lens (including the inside of the fixture body).

### Manager parameter packing

```hlsl
float4 _VRSLVolStepCount;   // x = max steps per light, y = fog coupling flag,
                            // z = 1 / sample spacing (m), w = HG anisotropy g
float4 _VRSLVolDensity;     // x = base density, y = noise scale, z = noise scroll, w = noise strength
float4 _VRSLVolFogTint;     // xyz = tint, w = global intensity multiplier
Texture3D<float> _VRSLVolNoise;         // the baked density field, bound per camera
RWStructuredBuffer<uint> _VRSLVolStats; // the four counters, written only while _VRSLVolCollectStats is set
```

Uploaded once per frame as global vectors in the volumetric pass `SetRenderFunc`.

---

## Depth requirements

The package reads scene depth in three passes and asks the pipeline for it, which is
the supported route.

**No light of any kind is required.** Depth is a pipeline setting, not something a
scene light produces. A room with no lights in it at all still lights surfaces and
still renders beams. (The Built-in pipeline did need a light present, which is why
older VRSL had a "depth light" prefab and a requirement toggle. Neither means anything
here and both are gone.)

**Depth priming may be on or off.** A project is free to choose, and the package is
correct either way, in Forward and Forward+.

**Whether priming runs with MSAA on depends on the URP a project ships.** Stock URP
requires a single-sample target for it, so a renderer set to `Forced` with MSAA above 1x
renders as though priming were `Disabled` — as it does under Deferred, on a camera that
is not the first to write depth, and on WebGL. A project vendoring its own URP may have
removed that condition, and then priming really is running on a multisampled camera.
Both are worth knowing before concluding a shader is fine: on stock URP a depth pass that
disagrees with its forward pass costs nothing on an MSAA target and takes the geometry
away the moment somebody turns MSAA off. And on a URP that primes with MSAA, anything
that asks for `ScriptableRenderPassInput.Normal` on that camera fails the whole frame,
because the depth-normals prepass cannot draw a single-sample normals target beside a
multisampled depth. VRSL never asks in that configuration; a renderer feature that does
will take the camera down on its own.

**A custom opaque shader has to hold up its end.** With priming on, URP renders a depth
prepass and then draws opaque geometry with an `Equal` depth test. Any shader whose
depth passes do not reproduce its forward pass exactly is culled from the frame — not
drawn wrong, drawn not at all. So a shader rendering in the opaque queue range needs
`DepthOnly` and `DepthNormals` passes whose vertex stage matches its forward one,
including any vertex displacement, alpha clipping, or conditional collapse to
degenerate geometry. Both of them: URP runs one prepass or the other depending on
whether anything in the frame has asked it for a normals texture — screen-space
ambient occlusion and screen-space decals both do — and priming tests against
whichever one ran, so a shader with only `DepthOnly` is fine until the project turns
SSAO on. That is a URP requirement rather than a VRSL one; every shader this package
ships already satisfies it.

The symptom when one does not is a fixture body that disappears, which reads as a
culling or LOD fault rather than a depth one — so it is worth checking rather than
guessing. **`VRSL → URP → Validate Renderer Setup`** reports the priming mode, whether
the prepass layer mask covers the layers your fixtures are on, and any VRSL shader in
the open scene that draws opaque without both depth passes.

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
- **Per-pixel light cost scales with lights-per-tile, not fixture count.** The tile cull is what bounds it; without `lightCullShader` assigned, both fullscreen passes fall back to iterating every fixture on every pixel, and the volumetric pass tests a march span for each one.
- **Geometry cost is the surface prepass.** Up to two extra opaque draws per camera: one where URP's depth-normals prepass is read instead of drawn (see *Where the normals come from*), which on a depth-primed single-sample camera is the common case. One manager draws it however many are active. In a scene whose opaque cost is dominated by avatars this is the term that grows with occupancy rather than with rig size.
- **The decode compute is negligible.** One workgroup per 64 fixtures, well under 1 ms at any practical fixture count.

No measured sweep is published with the package. The `RealtimeLightProfiling` sample builds a deterministic scene for one; run it before tuning anything above.

---

## Camera Selection

VRSL decides per camera whether to inject its passes, entirely from what the package
already owns — no host cooperation and no assumptions about how the surrounding
application configures its cameras.

Always skipped, regardless of settings:

- `CameraType.Reflection` and `CameraType.Preview`.
- Cameras registered by `VRSL_CameraConfigurator`, i.e. VRSL's own DMX screen readers.
- Cameras whose `targetTexture` is a render texture the manager consumes.

The last two are correctness, not cost. The lighting pass blends `One One` onto the active
colour target; on a DMX reader camera that target is the RAW-values texture feeding the CRT
decode chain, so a fixture within range of the reader's screen quad would brighten the
decoded channel values. The failure surfaces as nonsense DMX rather than as a rendering
fault, which makes it expensive to trace.

Everything else is governed by `secondaryCameraMode` on the manager, which covers cameras
that render into a texture rather than to the player's view — mirrors, portals, camera props.
Each one runs the whole light path again (the prepass, the cull, the lighting pass and the
raymarch are all view-dependent), so this is the control that decides what a world with
mirrors pays:

| Policy | What a mirror gets | Cost, against the main view |
|---|---|---|
| `Match` | Lit exactly like the main view, at the scene's level. | The same again per mirror. |
| `Reduced` (default) | Lit one level below the scene: a scene at `High` renders mirrors at `Standard`; a scene at `Standard` renders them at `Low`, a level only mirrors get (see the quality table). A scene at `Off` has nothing below it and mirrors render at `Off`. | Beams stay in the mirror. At `Low` the march takes half the samples and there is no contact-shadow trace. |
| `SurfaceOnly` | Surface lighting runs, the volumetric raymarch doesn't. | The raymarch is by far the more expensive of the two. |
| `Skip` | No VRSL passes. | Nothing, and a mirror pointed at the rig shows it. |

The policy is decided per camera, per frame, from the camera alone, so mirrors that appear
and disappear at runtime need nothing reset. A camera with no `targetTexture` is always
treated as the player's view, including under XR where the swapchain is handled outside the
camera. A camera that renders into a texture and is nonetheless the player's view, a stream
or spectator camera whose texture goes to a screen, can be registered with
`VRSLCameraFilter.RegisterMainView` and is then lit in full at the scene's level whatever the
policy says. `Validate Renderer Setup` names the level each camera in the open scene would render
at, and the benchmark rows carry it for the camera they measured.

---

## Known Limitations

- **No shadow casting beyond the near field.** This pipeline bypasses Unity's `Light` component to avoid the per-light shadow atlas cost, so a fixture lights every surface within range regardless of what stands between them. Screen-space contact shadows (see above) cover the near field off the depth buffer, but only for geometry the camera can see and only within the trace distance — a wall across the room still doesn't block a beam.
- **No ambient occlusion term** in the surface response. The BRDF runs against albedo, smoothness and metallic only.
- **No smoothness or metallic maps.** The surface prepass samples the scalar material properties, so a surface with a metallic/smoothness texture is lit against its uniform values.
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
| `Runtime/Scripts/VRSLSurfacePrepass.cs` | Towneh.VRSL.URP | Normals + albedo/material prepass (shared) |
| `Runtime/Scripts/VRSLTileCullPass.cs` | Towneh.VRSL.URP | Tiled light culling pass and `IVRSLLightSource` (shared) |
| `Runtime/Scripts/VRSLCameraFilter.cs` | Towneh.VRSL.URP | Per-camera inject/skip decision (see Camera Selection) |
| `Runtime/Scripts/VRSLGoboWheel.cs` | Towneh.VRSL.URP | Gobo slice packing into the `_VRSLGobos` array |
| `Runtime/Scripts/VRSLDiagnostics.cs` | Towneh.VRSL.URP | Runtime state readout — fixture counts, tile occupancy, pass activity |
| `Runtime/Scripts/VRSLTrussDmx.cs` | Towneh.VRSL.URP | Decoder for the Truss DMX record (SEI user data) into channel-source blocks; verifies CRC and framing |
| `Runtime/Integrations/Basis/BasisVideoToVRSLDMX.cs` | Towneh.VRSL.URP.Basis | Feeds the DMX decode chain from a `BasisMediaPlayer` stream |
| `Runtime/Integrations/Basis/BasisUserDataToVRSLDMX.cs` | Towneh.VRSL.URP.Basis | Channel source fed from the DMX records a `BasisMediaPlayer` stream carries as SEI user data |
| `Runtime/Integrations/Basis/BasisVideoRenderTextureOutput.cs` | Towneh.VRSL.URP.Basis | Corner-UV blit for a DMX grid occupying part of a larger frame |
| `Editor/VRSL_URPRendererSetup.cs` | Towneh.VRSL.URP.Editor | The scene-level "Add Light Manager" menu (DMX); resolves the package's own shader and compute references. Edits scene contents only, never project assets |
| `Runtime/Shaders/Compute/VRSLDMXLightUpdate.compute` | — | DMX compute kernel |
| `Runtime/Shaders/Compute/VRSLAudioLinkLightUpdate.compute` | — | AudioLink compute kernel |
| `Runtime/Shaders/Surface/VRSLDeferredLighting.shader` | — | Fullscreen surface lighting pass (shared) |
| `Runtime/Shaders/Surface/VRSLSurfaceProperties.shader` | — | Override shader for the albedo/material prepass |
| `Runtime/Shaders/Compute/VRSLLightCull.compute` | — | Tiled light-culling kernel (shared) |
| `Runtime/Shaders/Surface/VRSLVolumetricLighting.shader` | — | Raymarched volumetric pass (shared) |
| `Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl` | — | Struct definitions, attenuation, volumetric evaluation, gobo |
| `Runtime/Shaders/Shared/VRSLSurfaceBRDF.hlsl` | — | Material capture read-back and BRDF surface evaluation |
| `Runtime/Shaders/Shared/VRSLTileCulling.hlsl` | — | Read side of the per-tile light list |
| `Editor/VRStageLighting_DMX_RealtimeLightEditor.cs` | Towneh.VRSL.URP.Editor | DMX custom inspector |
| `Editor/VRStageLighting_AudioLink_RealtimeLightEditor.cs` | Towneh.VRSL.URP.Editor | AudioLink custom inspector |
| `Editor/VRSL_AudioLinkURPSetup.cs` | Towneh.VRSL.URP.Editor | Scene-wide AudioLink fixture configuration utility |
| `Editor/VRSL_LegacyToUrpMigration.cs` | Towneh.VRSL.URP.Editor | BIRP → URP fixture migration, sibling and in-place modes (compiled only when the upstream package is present) |
| `Editor/VRSL_ShaderValidation.cs` | Towneh.VRSL.URP.Editor | `VRSL → URP → Validate Shaders` — reports shaders that failed to compile |
| `Editor/VRSL_MissingScriptCleaner.cs` | Towneh.VRSL.URP.Editor | `[InitializeOnLoad]`; scrubs missing-script slots in VRSL subtrees on scene open, in memory only |
| `Editor/VRSL_EditorHeader.cs` | Towneh.VRSL.URP.Editor | Shared logo + version-bar helper |
| `Editor/VRSL_URPProfilingSampleMenu.cs` | Towneh.VRSL.URP.Editor | `VRSL → Profiling → Import Profiling Sample` menu entry |
| `Samples~/RealtimeLightProfiling/` | Towneh.VRSL.URP.Profiling, Towneh.VRSL.URP.Profiling.Editor | Opt-in sample: scene builder window and synthetic CRT-bypass DMX source for benchmarking the realtime light path |
