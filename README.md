# VRSL for URP

Stage lighting for Unity 6 worlds on the Universal Render Pipeline. Moving heads, washes, pars and blinders that light the room the way real fixtures do, with beams in the air, driven either from a lighting desk over DMX or from the music through [AudioLink](https://github.com/llealloo/audiolink).

It is a standalone fork of [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) by **AcChosen**, rebuilt for URP. The original package targets the Built-in pipeline and VRChat; this one targets URP and any host, and installs alongside the original rather than replacing it, so a world can move over one fixture at a time.

**Where to read next**

| You want to | Go to |
|---|---|
| Get a rig lit in ten minutes | [Your first rig](#your-first-rig) below |
| Step-by-step guides in plain language | The [wiki](https://github.com/towneh/vrsl-urp/wiki) |
| Every inspector field, for both data paths | [`Documentation~/URP-Fixture-Configuration-Guide.md`](Documentation~/URP-Fixture-Configuration-Guide.md) |
| DMX arriving as bytes rather than as a video grid, including from a Basis media player | [`Documentation~/DMX-Channel-Sources.md`](Documentation~/DMX-Channel-Sources.md) |
| See every channel's value live | [`Documentation~/DMX-Monitor.md`](Documentation~/DMX-Monitor.md) |
| How it works inside, and its limits | [`Documentation~/URP-Realtime-Volumetric-Lights.md`](Documentation~/URP-Realtime-Volumetric-Lights.md) |
| What changed, and known issues | [`CHANGELOG.md`](CHANGELOG.md) |
| Verify a change to the package | [`TESTING.md`](TESTING.md) |

## What it does

- **Lights surfaces properly.** Each fixture is a real light on the floor, the walls and the avatars, with the surface's own colour and gloss, and screen-space contact shadows if you want them. Many fixtures cost what a few would in Unity's own light system, because there is no shadow map per light.
- **Puts beams in the air.** Raymarched volumetric cones, with gobos, haze and strobe, that read correctly against the geometry in front of and behind them.
- **Leaves your renderer alone.** The package injects its passes at runtime. It never edits your URP asset or renderer, so it works in projects where the renderer is not yours to change.
- **Takes DMX or audio.** DMX from a desk over Art-Net or OSC as a video grid, or as bytes carried inside a live video stream. AudioLink for a rig that reacts to the music with no desk at all.

## What you need

- Unity 6000.0 or newer
- Universal Render Pipeline 17.0 or newer, Forward or Forward+
- AudioLink 3.1.2 or newer. It is a package dependency, so it must be installed even for a DMX-only rig.
- A desktop-class GPU. The package uses compute shaders and a full-screen raymarch, and is built for desktop VR and flatscreen. Quest is not a target.

## Install

Package Manager, **Add package from git URL**:

```
https://github.com/towneh/vrsl-urp.git
```

Or, from a local clone, in `Packages/manifest.json`:

```json
"town.mr.vrsl-urp": "file:C:/path/to/vrsl-urp"
```

There is no renderer feature to add and no URP setting to find. The one thing worth running after install is **VRSL → URP → Validate Renderer Setup**, which reads your pipeline asset and open scene and says in plain terms whether anything needs changing. It changes nothing itself.

## Your first rig

1. **Open an example scene** to see a working rig before building your own.

   | Scene | Folder |
   |---|---|
   | `VRSL-ExampleScene-AudioLink-URPRealtimeLights` | `Runtime/Example Scenes/AudioLink-Scenes/` |
   | `VRSL-ExampleScene-EditorViaOSC-Horizontal-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |
   | `VRSL-ExampleScene-EditorViaOSC-Vertical-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |

   The AudioLink scene lights up as soon as audio plays. The DMX scenes need a desk or a DMX sender to feed them; without one they stay dark, which is correct rather than broken.

2. **Add a light manager** to your own scene: **VRSL → URP → Add Light Manager to Active Scene**. One manager per scene, per data path. It arrives with every shader and texture reference filled in, and its inspector shows a single status line saying whether it is wired. If a reference ever goes missing, **VRSL → URP → Repair Manager Wiring** puts it back.

3. **Add fixtures.** Drag prefabs in from `Runtime/Prefabs/DMX/` or `Runtime/Prefabs/AudioLink/`, or add `VRStageLighting_DMX_RealtimeLight` or `VRStageLighting_AudioLink_RealtimeLight` to fixture geometry of your own. For an AudioLink rig, **VRSL → URP → AudioLink Config → Setup AudioLink Realtime Lights in Scene** adds and wires the component on every mover in the scene at once.

4. **Patch them.** A DMX fixture needs a universe and a start channel that match the desk. The prefabs use VRSL's 13-channel layout; five-channel statics have their own. AudioLink fixtures pick a frequency band and a colour source instead.

5. **Press play.** Surfaces light and beams appear. If they do not, select the manager, right-click its header and run **VRSL Diagnostics**: it says which stage went quiet.

The per-field detail for every fixture type is in the [fixture guide](Documentation~/URP-Fixture-Configuration-Guide.md).

## The controls that matter

Everything that decides what the package costs sits on the manager, and there are few of them on purpose. The artistic controls (density, tint, anisotropy, intensity, fog coupling) change the look at a fixed price and are yours to set freely.

| Control | Choices | What it decides |
|---|---|---|
| **Quality** | `Off`, `Standard`, `High` | How much of the frame the package may spend. `Standard` suits most worlds. `High` marches the beams more finely and traces contact shadows further. `Off` keeps surfaces lit and removes the beams and the shadows. What each level costs is fixed in code, so a level costs the same in every scene. |
| **Secondary cameras** | `Match`, `Reduced`, `SurfaceOnly`, `Skip` | What mirrors, portals and camera props get. Each one runs the whole light path again, so this is where a world with mirrors spends or saves. `Reduced`, the default, lights them one level below the scene so the beams stay at a lower price. `SurfaceOnly` drops the beams in mirrors. `Skip` drops everything. |
| **Contact shadow strength** | 0 to 1 | Screen-space shadows from the geometry the camera can see. Off by default because it is the most expensive term in the surface pass. |
| **Lit surfaces** | Layer mask | Which layers keep their own colour, gloss and normal maps when lit. Anything left out is still lit, as a plain mid-grey surface. Leave at Everything unless a layer is expensive to draw. |

**VRSL → URP → Performance → Analyse This Scene** measures what the package costs in the scene you have open, in milliseconds, at each quality level, and says what turning things down would give back.

## Checking it works

| Tool | What it answers |
|---|---|
| **VRSL → URP → Validate Renderer Setup** | Which renderer each camera uses, its depth-priming and MSAA modes, whether the prepass covers the layers your fixtures sit on, what each camera renders at under the secondary-camera policy, and any opaque shader missing a depth pass. Read-only. |
| **VRSL → URP → Validate Shaders** | Whether every package shader compiled. A full-screen shader that failed draws nothing rather than drawing wrong, so this is the first check when the scene is dark. |
| **VRSL Diagnostics** (right-click the manager in play mode) | Whether the decode produced data, whether the tile cull is keeping it, whether the prepass is feeding the surfaces, and what the beams took. |
| **VRSL → URP → DMX Config → DMX Monitor** | Every channel of a universe as a live grid, which of the two DMX paths the fixtures are really reading, and how long ago each universe was last heard from. |
| **VRSL → URP → Performance → Analyse This Scene** | What the package costs here, and whether the scene fits a refresh-rate budget at each level. |

## Troubleshooting

Start with **VRSL Diagnostics** on the manager. Most faults in a lighting pipeline look the same from outside, nothing is lit, and the report separates them.

| Symptom | Likely cause |
|---|---|
| Nothing lit at all | A full-screen shader did not compile. Run **Validate Shaders**. |
| Everything lit a flat mid-grey | `surfacePropertiesShader` is empty on the manager, or the surfaces are on a layer left out of **Lit surfaces**. |
| No beams | `volumetricShader` empty, `volumetricIntensity` at 0, or quality at `Off`. |
| Beams show visible stepping | Set quality to `High`. Wide cones, long throws and dense haze show stepping before a narrow spot does. |
| Fixture bodies dark while the light they cast works | The manager publishes the DMX grid textures for the bodies. Check its texture slots, including the strobe timer. |
| DMX values plausible but wrong | Addressing. A fixture patched at the wrong start channel reads its neighbour's channels and still looks like a lighting decision. Open the DMX Monitor: a patch off by one shears diagonally across the grid. Check `use5ChannelMode` matches how the fixture is patched. |
| A DMX fixture is half as bright as it was in an older version | Intended. `maxIntensity` is now on the same scale as a URP spot light's Intensity. Double it, or set `curveMod` to 1. |
| Beams missing in a mirror | The secondary-camera policy on the manager. `Match` lights mirrors like the main view; `Reduced` keeps beams at a lower level; `SurfaceOnly` and `Skip` remove them. |
| A fixture body vanishes when depth priming is on | Its shader lacks a depth pass that matches its forward pass. **Validate Renderer Setup** names any opaque shader in the scene missing one. Every shader this package ships has both. |
| Frame time worse than expected | In order: quality to `Standard` or `Off`, contact shadow strength to 0 if you do not need it, and check `lightCullShader` is assigned. Then **Analyse This Scene**. |
| Par, blinder, laser or discoball bodies wrong in VR | Known issue, see `CHANGELOG.md`. Moving heads, surface lighting and beams are unaffected. |

## Where the DMX comes from

Two routes, and a scene can carry both.

- **A video grid.** A frame of video encodes channel values as colours, the package decodes it on the GPU, and the fixtures read the result. Any video source that ends up as a texture will do, which is how DMX reaches a world with no other way in.
- **Bytes.** Where the values already arrive as data, a channel source hands them straight to the manager and nothing is encoded or decoded. [Truss](https://github.com/towneh/Truss) carries a desk's Art-Net inside a live H.264 stream, and a Basis media player showing that stream feeds the rig through **VRSL → URP → DMX Config → Add Basis DMX Record Source (SEI)**. The fixtures read the exact bytes the desk sent, every record carries a checksum, and no part of the picture is given up to a grid.

[`Documentation~/DMX-Channel-Sources.md`](Documentation~/DMX-Channel-Sources.md) covers both, how to frame a grid that arrives as part of a larger picture, and how to write a source of your own.

## Migrating from Built-in

Both packages can be installed at once. Asset GUIDs, shader names (`VRSL-URP/…` against `VRSL/…`), C# namespaces (`VRSL.URP` against `VRSL`) and DMX shader globals (`_VRSLU_DMX*` against `_Udon_DMX*`) are all distinct, so the two run side by side without touching each other.

Two menu items appear when the original package is present:

- **VRSL → URP → Migrate Scene Fixtures (Add URP Siblings)** places the matching URP fixture beside each original at the same transform and leaves the original in place, so you can compare the two before deleting the originals.
- **VRSL → URP → Convert Custom Fixtures In-Place (Component + Material)** swaps the component and material on fixtures you built yourself.

Both match on the source prefab and can be run again safely. Removing the original package afterwards leaves missing-script slots on fixtures that referenced it; those are cleaned up in memory whenever a scene opens, without marking the scene dirty.

## Attribution

Substantial portions of the shaders, prefabs, fixture meshes, textures, decode chain and authoring components derive from [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) by **AcChosen**, MIT-licensed, with full credit and copyright to AcChosen. The URP realtime light path and the standalone restructuring on top of that base are this fork's contribution. See `LICENSE.md` and `NOTICE.md` for the per-component breakdown.

## Licence

MIT. See `LICENSE.md`.
