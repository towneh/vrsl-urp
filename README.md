# VRSL URP

Realtime stage lighting and raymarched volumetric beams for Unity 6 / URP 17+, driven from DMX or AudioLink data. Genuine scene illumination from large fixture counts with no per-light shadow atlas cost, real PBR surface response against each shader's own textures, and no URP renderer settings touched.

This is a standalone fork of [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) by **AcChosen**, rebuilt for URP. It carries its own asset GUIDs and namespaces so it installs *alongside* the upstream Built-in-pipeline package rather than replacing it — which is what makes a BIRP → URP migration incremental instead of all-at-once. See [Migrating from Built-in](#migrating-from-built-in).

## Contents

| Document | For |
|---|---|
| This README | Installing, first scene, troubleshooting |
| [`Documentation~/URP-Fixture-Configuration-Guide.md`](Documentation~/URP-Fixture-Configuration-Guide.md) | Authoring fixtures — every inspector field, both data paths |
| [`Documentation~/URP-Realtime-Volumetric-Lights.md`](Documentation~/URP-Realtime-Volumetric-Lights.md) | Architecture — pipeline, GPU structs, performance model, limitations |
| [`TESTING.md`](TESTING.md) | The verification matrix, if you're changing the package |
| [`CHANGELOG.md`](CHANGELOG.md) | What changed, and the current known issues |

## Requirements

- Unity 6000.0 LTS or newer
- Universal Render Pipeline 17.0+
- [AudioLink](https://github.com/llealloo/audiolink) 3.1.2+ — a hard package dependency, so it must be installed even for a DMX-only rig

## Installation

**Package Manager → Add package from git URL:**

```
https://github.com/towneh/vrsl-urp.git
```

**Or by path**, if you've cloned it locally — in `Packages/manifest.json`:

```json
"town.mr.vrsl-urp": "file:C:/path/to/vrsl-urp"
```

Nothing else to configure. The package never reads, writes, or recommends URP asset or URP Renderer asset settings — no Renderer Feature to add, no depth-texture toggle to find. Passes are injected at runtime through `RenderPipelineManager.beginCameraRendering`, which is what lets it work in projects where the renderer asset isn't yours to author.

## First scene

1. Open one of the example scenes to see a working rig before building your own:

   | Scene | Path |
   |---|---|
   | `VRSL-ExampleScene-AudioLink-URPRealtimeLights` | `Runtime/Example Scenes/AudioLink-Scenes/` |
   | `VRSL-ExampleScene-EditorViaOSC-Horizontal-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |
   | `VRSL-ExampleScene-EditorViaOSC-Vertical-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |

2. In your own scene, **VRSL → URP → Add Light Manager to Active Scene**. That creates the manager with every shader reference assigned. Both menu utilities are idempotent, so re-running them is safe.

3. Drop in URP fixture prefabs from `Runtime/Prefabs/DMX/` or `Runtime/Prefabs/AudioLink/`, or add `VRStageLighting_DMX_RealtimeLight` / `VRStageLighting_AudioLink_RealtimeLight` to your own fixture geometry.

4. For an AudioLink rig, **VRSL → URP → Setup AudioLink Realtime Lights in Scene** adds and wires the component on every AudioLink mover spotlight in one pass.

Per-field authoring is in the [fixture configuration guide](Documentation~/URP-Fixture-Configuration-Guide.md).

## Migrating from Built-in

Both packages can be installed at once. Asset GUIDs, shader picker namespaces (`VRSL-URP/…` against upstream's `VRSL/…`), C# namespaces (`VRSL.URP` against `VRSL`) and runtime DMX globals (`_VRSLU_DMX*` against `_Udon_DMX*`) are all distinct, so the two run their own pipelines in parallel without collision.

Two migration utilities appear when the upstream package is present:

- **VRSL → URP → Migrate Scene Fixtures (Add URP Siblings)** inserts the matching URP fixture beside each upstream fixture at the same world transform, and leaves the original in place. The URP siblings light the scene under URP while the originals keep rendering under the legacy path, so you can compare the two before committing. Delete the originals when you're happy.
- **VRSL → URP → Convert Custom Fixtures In-Place (Component + Material)** swaps the component and material on fixtures you built yourself rather than from a stock prefab.

Both match on source-prefab GUID and are idempotent, so a second run is a no-op and prefab renames in either package don't break them.

Removing the upstream package afterwards leaves missing-script slots on the fixtures that referenced it. Those are scrubbed automatically whenever a scene opens — in memory only, so no scene is marked dirty and no save prompt appears.

## Troubleshooting

**Start here: select the manager in play mode, right-click the component header, and run `VRSL Diagnostics`.** Most failures in this pipeline look identical from outside — nothing is lit — and that report separates a failed decode from an empty tile cull from a shader that silently didn't compile.

| Symptom | Likely cause |
|---|---|
| Everything lit a flat neutral grey | `surfacePropertiesShader` unassigned on the manager. Without it there's no albedo capture, so every surface shades as a neutral dielectric. |
| Nothing lit at all | A fullscreen shader failed to compile — it draws nothing rather than drawing wrong. Run **VRSL → URP → Validate Shaders**. |
| No volumetric cones | `volumetricShader` unassigned, or `volumetricIntensity` at 0. |
| Cones show visible stepping | `volumetricStepCount` too low for the beam. Wide cones, long throws and dense haze need more steps than a narrow spot. |
| Fixture bodies dark, but the light they cast works | The manager publishes the DMX grid CRTs itself; check its CRT slots are populated, including `dmxStrobeTimerTexture`. |
| DMX decodes plausible but wrong values | Channel addressing. Check `use5ChannelMode` matches how the fixture is patched — a 5-channel fixture read as 13-channel picks up its neighbours' channels. |
| A DMX fixture is half as bright as it used to be | Intended. The dimmer curve now peaks at exactly 1, so `maxIntensity` matches a URP spot light's Intensity. Double `maxIntensity`, or set `curveMod` to 1. |
| Beams missing in mirrors or camera props | `secondaryCameraMode` on the manager. `Full` lights them like the main view; `SurfaceOnly` drops the raymarch; `Skip` drops both. |
| Frame time worse than expected | In order: lower `volumetricStepCount`, confirm `volumetricResolution` is `Half`, check `contactShadowStrength` is 0 if you don't need it, and make sure `lightCullShader` is assigned — without it both fullscreen passes iterate every fixture on every pixel. |
| Par, blinder, laser or discoball bodies wrong in VR | Known issue, see `CHANGELOG.md`. Moving heads, surface lighting and volumetrics are unaffected. |

For benchmarking rather than debugging, **VRSL → Profiling → Import Profiling Sample** brings in a scene builder that generates a deterministic rig of N matched fixtures with a fixed camera pose.

## Attribution

Substantial portions of the shaders, prefabs, fixture meshes, textures, CRT decode chain and authoring components derive from [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) by **AcChosen**, MIT-licensed — full credit and copyright to AcChosen. The URP realtime light path and the standalone restructuring on top of that base are this fork's contribution. See `LICENSE.md` and `NOTICE.md` for the per-component breakdown.

## License

MIT — see `LICENSE.md`.
