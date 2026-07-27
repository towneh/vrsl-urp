# VRSL URP Lighting — Effectiveness Review

An assessment of the URP realtime light path against the four things VRSL has to
land: data reactivity, cost at scale, surface accuracy, and the migration away from
the Built-in Render Pipeline model. Written against package version `0.1.0`.

This is a design-review document, not a reference. For how the pipeline works, see
`URP-Realtime-Volumetric-Lights.md`; for per-fixture authoring, see
`URP-Fixture-Configuration-Guide.md`.

---

## Summary

| Requirement | Verdict |
|---|---|
| DMX + AudioLink reactivity | Solid. The strongest part of the package. |
| Cost at 100+ fixtures | Architecture is right, shader implementation is not. No light culling of any kind. |
| Surface accuracy against PBR materials | Not implemented. No albedo, no BRDF, no specular, no occlusion. |
| Evolution from Built-in | Real progress, but two DMX decode paths still run in parallel. |

---

## 1. Data reactivity

This part works. `VRSLDMXLightUpdate.compute` decodes the CRT chain on the GPU at one
thread per fixture, and pan/tilt goes through a Rodrigues rotation in the same kernel.
There is no per-fixture CPU decode, no `Light` component write, and no GPU→CPU readback
on either path. DMX config uploads only when a fixture is marked dirty; AudioLink uploads
`N × 112` bytes per frame as a single `SetData`. Latency is one frame behind the CRT
chain, which is well inside what stage work needs.

Three defects worth fixing:

**Colour space is inconsistent between the two data sources.** The AudioLink manager
converts emission with `Color.linear` before upload. The DMX path writes the decoded
channel values straight into `colorAndIntensity.rgb` with no conversion. A DMX fixture
and an AudioLink fixture set to the same nominal colour do not match, and the error is
largest in the mid-tones.

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
3. **Restructure the volumetric integration.** Accumulating scattering into a froxel volume
   once, rather than re-marching per screen pixel, decouples cost from resolution and opens
   the door to temporal reprojection.
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

Implemented on top of the reviewed version:

- The surface-prepass route from section 3, with URP's BRDF replacing the flat additive
  accumulation. The scene-colour proxy is gone.
- Tiled light culling (section 2, fix 1) and the early rejections (fix 2).

Still open, in the order they are worth doing:

- **Record a profiling sweep.** Nothing above has been measured. The sample builds the scene
  for it.
- **Occlusion.** Screen-space contact shadows for the near field, a small pool of real
  `Light` components for hero fixtures.
- **Froxel volumetric integration** (section 2, fix 3) to decouple cost from resolution.
- **Camera filtering and prepass deduplication** (fixes 5 and 6) — mirrors currently pay the
  full stack, and the prepass runs twice when both managers are active.
- **The single decode path** from section 4, which retires `curveMod` and the duplicated
  channel constants.
- **The DMX linear-colour conversion** and a defined intensity unit, from section 1.
- **Opt-in VRSL Lit materials** for surfaces the world author controls, layered on top of the
  prepass baseline.
