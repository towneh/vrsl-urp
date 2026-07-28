# VRSL URP Lighting — Effectiveness Review

An assessment of the URP realtime light path against the four things VRSL has to
land: data reactivity, cost at scale, surface accuracy, and the migration away from
the Built-in Render Pipeline model. Written against package version `0.1.0`.

This is a design-review document, not a reference. For how the pipeline works, see
`URP-Realtime-Volumetric-Lights.md`; for per-fixture authoring, see
`URP-Fixture-Configuration-Guide.md`.

---

## Summary

| Requirement | Verdict as reviewed | Where it stands now |
|---|---|---|
| DMX + AudioLink reactivity | Solid. The strongest part of the package. | Unchanged. |
| Cost at 100+ fixtures | Architecture is right, shader implementation is not. No light culling of any kind. | Tiled culling, early rejection, a 64-byte light struct and camera filtering have landed. Still unmeasured. |
| Surface accuracy against PBR materials | Not implemented. No albedo, no BRDF, no specular, no occlusion. | A surface prepass, URP's BRDF and contact shadows have landed. No light-perspective shadows; smoothness and metallic are scalars rather than maps. |
| Evolution from Built-in | Real progress, but two DMX decode paths still run in parallel. | Unchanged. The single decode path is still open. |

Sections 1–4 are the assessment as first written, kept as the baseline the work was planned
against. **Status** at the end records what has changed since.

---

## 1. Data reactivity

This part works. `VRSLDMXLightUpdate.compute` decodes the CRT chain on the GPU at one
thread per fixture, and pan/tilt goes through a Rodrigues rotation in the same kernel.
There is no per-fixture CPU decode, no `Light` component write, and no GPU→CPU readback
on either path. DMX config uploads only when a fixture is marked dirty; AudioLink uploads
`N × 112` bytes per frame as a single `SetData`. Latency is one frame behind the CRT
chain, which is well inside what stage work needs.

Two defects worth fixing, plus one apparent one that isn't:

**Colour handling differs between the two data sources, and both are correct.** The AudioLink
manager converts emission with `Color.linear` before upload; the DMX path writes decoded
channel values straight into `colorAndIntensity.rgb`. That reads like an inconsistency but
isn't. The grid render textures are `R8G8B8A8_UNorm` and `R32G32B32A32_SFloat` — both linear,
so sampling applies no sRGB conversion and the decoded values are emitter drive levels, where
0.5 already means half radiance. AudioLink's `emissionColor` is an author-picked sRGB colour,
where `.linear` is the right conversion. Two different quantities, each handled correctly;
converting either to match the other would break it. A desk level and a picked colour still
won't visually match at the same nominal mid-tone, but that is a property of the inputs.

**The channel decode rests on undocumented constants.** The `-0.015` and `-0.001915` UV
offsets, and the 13th-channel correction table covering ranges 90–101, 160–205, 326–404,
676–819 and ≥1339, are empirical values carried over from the Built-in shader path. They
are duplicated verbatim across `GetDMXValue` and `GetDMXValueRaw`. They hold only for the
grid resolution they were derived against, and when they stop holding the read lands on
the wrong cell and decodes to black with no diagnostic.

**`curveMod` couples a physical quantity to an artistic one.** The compute multiplies
light intensity by `intensity × (1 + (curveMod − 1) × intensity)` so the render-pass light
tracks the fixture-body surface shader's dimmer curve. The light is being bent to match an
emissive material rather than the other way round, and the coupling has to be maintained
by hand across two languages. See section 4.

---

## 2. Cost at scale

### What the architecture gets right

Bypassing Unity's `Light` component removes the cost that actually matters. There is no
per-light shadow atlas, no per-object light list, no CPU-side light culling, and no
`MaterialPropertyBlock` churn per cone. One compute dispatch covers any fixture count.
The render-graph integration is careful: explicit `AccessFlags`, the
`AllowPassCulling(false)` opt-out for the buffer that is consumed through a global rather
than a tracked read, and per-eye `Tex2DArray` slices on every transient target. That
foundation genuinely supports 100+ fixtures.

### What the shaders get wrong

There is no light culling. `_VRSLLightCount` is set to the full fixture count and every
shader loops all of it, on every pixel.

In the surface pass, `VRSL_EvaluateLight` has no range rejection — the volumetric variant
carries one, the surface variant does not — and the caller applies `SampleGobo`
unconditionally, so the gobo texture fetch runs even where the light's contribution is
already zero. `SampleGobo` only short-circuits on `goboIdx < -0.5`, and the DMX compute
clamps every spot fixture into slot `1..N` whenever the manager has gobo textures
assigned. With gobos in use that is one texture-array fetch per light per pixel across the
whole frame.

The volumetric pass repeats the same unconditional fetch inside the step loop. At the
default 32 steps and 100 fixtures that is 3,200 light evaluations and up to 3,200 texture
fetches per half-resolution pixel, per eye. This is the dominant scaling term and it is
unbounded in fixture count, which is precisely what the requirement rules out.

### Ordered fixes

1. **Cull lights per screen tile.** Build a per-tile light list in the compute stage and
   have both passes iterate that list. Per-pixel light count drops from the scene total to
   the handful actually overlapping the tile. This is the change that makes the target
   fixture count real; everything else is small next to it.
2. **Reject early.** Add the range test to `VRSL_EvaluateLight` and guard the gobo fetch
   behind a non-zero contribution in both shaders.
3. **Restructure the volumetric integration.** *Evaluated and not recommended.*
   Accumulating scattering into a froxel volume once, rather than re-marching per screen
   pixel, does decouple cost from resolution. It also cannot hold the edges of hard-edged
   spotlight cones: a view-aligned grid blurs through depth discontinuities where the
   half-res path's bilateral upsample explicitly rejects across them. Matching the raymarch's
   edge quality needs a volume dense enough to cost what the raymarch costs, plus the volume's
   own memory. The raymarch modes stay as they are. This also weakens the case for temporal
   reprojection on a volume, since accumulation cannot recover lateral resolution the grid
   never had, and moving beams are the worst case for it.
4. **Shrink `VRSLLightData`.** 80 bytes re-fetched per light per step is the bandwidth term.
5. **Filter cameras.** `OnBeginCameraRendering` skips only `Reflection` and `Preview`.
   Mirror and portal cameras are `CameraType.Game`, so every mirror in a scene currently
   pays the full stack.
6. **Deduplicate the prepass.** The surface prepass is a full extra opaque geometry pass,
   and it runs twice when both managers are active.
7. **Keep the gobo wheel on the GPU.** `BuildGoboArray` does a `ReadPixels` readback per
   slot and forces every gobo to 256².

### On measurement

The profiling sample is well built — deterministic scene builder, CRT-bypass synthetic
source, and camera variants that separate the overlapping-cone worst case from the
best case. No results from it are recorded anywhere in the repository. Every performance
statement in the architecture document is reasoning from structure rather than
measurement. A recorded sweep should exist before any further tuning.

---

## 3. Surface accuracy

This is the requirement that needs a change of approach rather than a fix.

The whole lighting model is one expression in `VRSLLightingLibrary.hlsl`:

```hlsl
return light.colorAndIntensity.xyz * light.colorAndIntensity.w
       * distAtten * spotAtten * NdotL;
```

blended `One One` onto the camera colour. Against what URP's `Lit` shader evaluates for
the same light:

| Term | URP Lit | VRSL |
|---|---|---|
| Diffuse albedo × (1 − reflectivity) | yes | **absent** |
| Specular lobe (GGX, smoothness, F0) | yes | absent |
| Metallic response | yes | absent |
| Ambient occlusion | yes | absent |
| Shadow attenuation | yes | absent |
| N·L, distance attenuation, spot cone | yes | yes |

**The missing albedo term is the visible symptom.** A white light on a red carpet adds
white, so the carpet washes towards white rather than reading as a lit red carpet. A black
surface under a spot glows grey. Texture detail disappears under any bright fixture,
because the pass adds a flat value on top of the surface instead of modulating it. Decoded
intensities routinely reach the hundreds, so the additive result also clips past the
tonemapper's shoulder and saturates to white regardless of the light's or the surface's
colour.

**The missing specular lobe is the other half.** A stage spot on a polished floor is
largely a specular event: the streak, the hotspot, the way it tracks the viewer. None of
that exists here, so even with albedo corrected the result would still read as painted on.

**The missing occlusion contradicts the requirement directly.** Every fixture currently
lights every surface in range through every wall, truss and body. The `lensClip` smoothstep
and the `emitterDepth` mechanism in `VRSL_SpotAttenuation` exist to stop light bleeding
through the inside of a fixture's own housing; they are compensation for the absent
occlusion, not features in their own right.

### Why the scene-colour proxy cannot close the gap

The `albedoTintStrength` path multiplies accumulated light by a snapshot of the composited
frame. That is the wrong quantity, in three independent ways:

- The snapshot is `albedo × (ambient + baked + realtime) + emissive + reflections`, so
  multiplying by it double-counts every other light in the scene.
- In a dark venue — the package's target scene — the snapshot is near black, so the tint
  drives VRSL light towards zero. A white wall in an unlit room has albedo near 0.8 and a
  scene colour near 0.02. The proxy is worst exactly where the package is used most.
- The snapshot is in post-exposure HDR space, not reflectance space.

### Direction

Getting real material data in front of the light evaluation is the only route to the
requirement. The options, with their trade-offs:

**URP Deferred and the real G-buffer.** Inject after the G-buffer and read albedo,
metallic/specular and normal/smoothness directly. Correct, and free in geometry cost. Ruled
out here: forward-only shaders never enter the G-buffer, so avatars would receive no light
at all, and it forces a renderer configuration on the host project.

**A VRSL-owned surface prepass.** Re-render opaques with a replacement shader that keeps
each material's own property values, writing albedo, smoothness and metallic into
VRSL-owned targets, then evaluate a real BRDF in the fullscreen pass. Costs one extra
opaque geometry pass. Covers any shader that follows the common base-texture naming,
avatars included. This is the route taken.

**An opt-in VRSL Lit path.** A shader include and a Shader Graph node that let a material
evaluate `_VRSLLights` inside its own forward pass, with full BRDF and no extra passes.
Exact, but only for surfaces the world author controls — which, for a venue floor, walls
and truss, is most of what carries the look. Worth layering on top later.

**Writing into URP's Forward+ light buffers.** Requires matching URP's CPU-side binning
against non-public surface. Not a foundation to build on.

Alongside whichever route: the intensity scale needs defining. `maxIntensity` is documented
as peak lux but is not in lux and is not consistent with URP's physical light units. Once
albedo enters the equation the useful range shifts anyway.

---

## 4. Evolution from Built-in

The direction is right, and the hard parts are done well. Per-pixel evaluation against a
depth-reconstructed world position beats projector decals. A single screen-space raymarch
beats per-fixture cone meshes, and terminating it against scene depth means geometry and
avatars silhouette out of the cone correctly, which the vertex-shader cones never managed.
The single-pass-instanced work is careful and correct: the explicit
`multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON`, per-eye array slices on every
transient target, `ColorMask RGB` so additive passes leave scene alpha alone, and guards on
the degenerate-normal path.

The gap is that the migration stopped halfway. Two independent DMX decode paths ship
together:

- the compute's `GetDMXValue`, feeding `VRSLLightData` and the render passes;
- the fixture-body surface shaders' `IndustryRead`, decoding the same CRTs from the
  `_VRSLU_DMX*` globals.

They have to agree bit for bit or a fixture's body and its light disagree on screen. The
`curveMod` term is what that costs in practice. Making the compute the single source of
truth, and having fixture-body shaders read the decoded buffer rather than re-decode the
grid, removes the duplicated constants and lets channel-layout work land in one place.

One naming point: `VRSLDeferredLighting.shader` is not deferred lighting — there is no
G-buffer and no material data behind it. Naming it for what it does would have surfaced the
albedo gap sooner.

---

## Status

### Landed

- **The surface-prepass route from section 3**, with URP's `InitializeBRDFData` + `DirectBRDF`
  replacing the flat additive accumulation. The scene-colour proxy is gone.
- **Tiled light culling** (fix 1) and the early rejections (fix 2).
- **Screen-space contact shadows**, off by default, closing the near-field half of the
  occlusion gap in section 3.
- **Camera filtering** (fix 5) and **prepass deduplication** (fix 6). Mirrors no longer pay the
  full stack, and the prepass runs once rather than twice when both managers are active.
- **`VRSLLightData` shrunk** (fix 4) from 80 bytes to 64.
- **The gobo wheel packed on the GPU** (fix 7). The per-slot `ReadPixels` readback is gone.
- **A defined intensity unit** (section 1). `maxIntensity` now sits on the same scale as a URP
  spot light's Intensity value, which halved DMX fixtures at full output.

### Open

- **Record a profiling sweep.** Nothing above has been measured, so section 2's verdict rests
  on structure rather than numbers. This is the largest remaining gap in the review. The
  profiling sample builds the scene for it.
- **The single decode path** from section 4, which would retire `curveMod` and the duplicated
  channel constants. Blocked on a specification for the grid image: GridReader ships as closed
  binaries, and the codebase carries three different `u` values for channel 13.
- **Smoothness and metallic maps.** The prepass captures scalars only, so a surface with a
  roughness map lights as though it were uniform.
- **Light-perspective shadows.** Contact shadows only occlude against geometry the camera can
  see and only within the trace distance, so a wall across the room still doesn't block a beam.

### Closed by decision

- **Froxel volumetric integration** (fix 3). Evaluated against the raymarch and rejected; the
  reasoning is on fix 3 in section 2.
- **A pool of real `Light` components for hero fixtures.** Ruled out — the package drives no
  Unity `Light` components, which is the property section 2 credits for the cost profile.
- **Opt-in VRSL Lit materials.** Ruled out — receiving VRSL light should not require a surface
  to adopt a VRSL-specific shader path.
