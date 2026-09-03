# VRSL for URP

Stage lighting for Unity 6 worlds on the Universal Render Pipeline. Moving heads, washes, pars and blinders light the room the way real fixtures do, with beams in the air. A lighting desk drives them over DMX, or the music drives them through [AudioLink](https://github.com/llealloo/audiolink).

The package is a standalone fork of [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) by **AcChosen**, rebuilt for URP. The original targets the Built-in pipeline and VRChat. This one targets URP and any host. It installs beside the original rather than replacing it, so a world can move over one fixture at a time.

**Where to read next**

| You want to | Go to |
|---|---|
| Get a rig lit in ten minutes | [Your first rig](#your-first-rig) below |
| Step-by-step guides in plain language | The [wiki](https://github.com/towneh/vrsl-urp/wiki) |
| Every inspector field, for both data paths | [Fixture Configuration Reference](https://github.com/towneh/vrsl-urp/wiki/Fixture-Configuration-Reference) |
| DMX as bytes rather than as a video grid, including from a Basis media player | [DMX Channel Sources](https://github.com/towneh/vrsl-urp/wiki/DMX-Channel-Sources) |
| See every channel's value live | [DMX Monitor](https://github.com/towneh/vrsl-urp/wiki/DMX-Monitor) |
| How it works inside, and its limits | [Architecture](https://github.com/towneh/vrsl-urp/wiki/Architecture) |
| What changed, and known issues | [`CHANGELOG.md`](CHANGELOG.md) |
| Verify a change to the package | [`TESTING.md`](TESTING.md) |

## What it does

- **Lights surfaces properly.** Each fixture lights the floor, the walls and the avatars with the surface's own colour and gloss. Screen-space contact shadows are available. Many fixtures cost what a few would in Unity's own light system, because there is no shadow map per light.
- **Puts beams in the air.** Raymarched volumetric cones with gobos, haze and strobe. The cones read correctly against the geometry in front of them and behind them.
- **Leaves your renderer alone.** The package injects its passes at runtime. It never edits your URP asset or renderer, so it works in projects where the renderer is not yours to change.
- **Takes DMX or audio.** DMX from a desk arrives as a video grid, or as bytes inside a live video stream. AudioLink drives a rig from the music with no desk at all.

## What you need

- Unity 6000.0 or newer
- Universal Render Pipeline 17.0 or newer, in Forward or Forward+ mode
- AudioLink 3.1.2 or newer. The package depends on it, so install it even for a DMX-only rig.
- A desktop-class GPU. The package uses compute shaders and a full-screen raymarch. Desktop VR and flatscreen are the targets. Quest is not.

## Install

In the Package Manager, choose **Add package from git URL** and paste:

```
https://github.com/towneh/vrsl-urp.git
```

Or, from a local clone, point `Packages/manifest.json` at the folder:

```json
"town.mr.vrsl-urp": "file:C:/path/to/vrsl-urp"
```

There is no renderer feature to add and no URP setting to find. After you install, run **VRSL → URP → Validate Renderer Setup** once. It reads your pipeline asset and the open scene, and it says in plain terms whether anything needs to change. It changes nothing itself.

## Your first rig

1. **Open an example scene.** See a working rig before you build your own.

   | Scene | Folder |
   |---|---|
   | `VRSL-ExampleScene-AudioLink-URPRealtimeLights` | `Runtime/Example Scenes/AudioLink-Scenes/` |
   | `VRSL-ExampleScene-EditorViaOSC-Horizontal-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |
   | `VRSL-ExampleScene-EditorViaOSC-Vertical-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |

   The AudioLink scene lights up as soon as audio plays. The DMX scenes need a desk or a DMX sender. Without one they stay dark, which is correct.

2. **Add a light manager to your scene.** Run **VRSL → URP → Add Light Manager to Active Scene**. Use one manager per scene for each data path. The manager arrives with every shader and texture reference filled in. Its inspector shows one status line that says whether it is wired. If a reference goes missing, run **VRSL → URP → Repair Manager Wiring**.

3. **Add fixtures.** Drag prefabs in from `Runtime/Prefabs/DMX/` or `Runtime/Prefabs/AudioLink/`. Or add `VRStageLighting_DMX_RealtimeLight` or `VRStageLighting_AudioLink_RealtimeLight` to fixture geometry of your own. For an AudioLink rig, run **VRSL → URP → AudioLink Config → Setup AudioLink Realtime Lights in Scene**. It adds and wires the component on every mover in the scene.

4. **Patch them.** Give each DMX fixture the universe and start channel that match the desk. The prefabs use VRSL's 13-channel layout. Five-channel statics have their own layout. Give each AudioLink fixture a frequency band and a colour source instead.

5. **Press play.** Surfaces light and beams appear. If they do not, select the manager and right-click its header. Run **VRSL Diagnostics**. It says which stage went quiet.

The per-field detail for every fixture type is in the [Fixture Configuration Reference](https://github.com/towneh/vrsl-urp/wiki/Fixture-Configuration-Reference).

## The controls that matter

The controls that set what the package costs sit on the manager, and there are few of them on purpose. The artistic controls change the look at a fixed price: density, tint, anisotropy, intensity and fog coupling. Set those freely.

| Control | Choices | What it decides |
|---|---|---|
| **Quality** | `Off`, `Standard`, `High` | How much of the frame the package may spend. `Standard` suits most worlds. `High` marches the beams more finely and traces contact shadows further. `Off` keeps surfaces lit and removes the beams and the shadows. Each level has a fixed cost, so a level costs the same in every scene. |
| **Secondary cameras** | `Match`, `Reduced`, `SurfaceOnly`, `Skip` | What mirrors, portals and camera props get. Each one runs the whole light path again, so this is where a world with mirrors spends or saves. `Reduced`, the default, lights them one level below the scene, so the beams stay at a lower price. `SurfaceOnly` drops the beams in mirrors. `Skip` drops everything. |
| **Contact shadow strength** | 0 to 1 | Screen-space shadows from the geometry the camera can see. Off by default, because it is the most expensive term in the surface pass. |
| **Lit surfaces** | Layer mask | Which layers keep their own colour, gloss and normal maps when lit. A layer left out is still lit, as a plain mid-grey surface. Leave it at Everything unless a layer is expensive to draw. |

**VRSL → URP → Performance → Analyse This Scene** measures what the package costs in the open scene. It reports milliseconds at each quality level, and it says what a lower level would give back.

## Checking it works

| Tool | What it answers |
|---|---|
| **VRSL → URP → Validate Renderer Setup** | Which renderer each camera uses, its depth-priming and MSAA modes, and whether the prepass covers the layers your fixtures sit on. Also what each camera renders at under the secondary-camera policy, and any opaque shader that lacks a depth pass. Read-only. |
| **VRSL → URP → Validate Shaders** | Whether every package shader compiled. A full-screen shader that fails draws nothing rather than drawing wrong. Run this first when the scene is dark. |
| **VRSL Diagnostics** (right-click the manager in play mode) | Whether the decode produced data, whether the tile cull keeps it, whether the prepass feeds the surfaces, and what the beams took. |
| **VRSL → URP → DMX Config → DMX Monitor** | Every channel of a universe as a live grid, which of the two DMX paths the fixtures read, and how long ago each universe was last heard from. |
| **VRSL → URP → Performance → Analyse This Scene** | What the package costs here, and whether the scene fits a refresh-rate budget at each level. |

## Troubleshooting

Start with **VRSL Diagnostics** on the manager. Most faults in a lighting pipeline look the same from outside: nothing is lit. The report separates them.

| Symptom | Likely cause |
|---|---|
| Nothing lit at all | A full-screen shader did not compile. Run **Validate Shaders**. |
| Everything lit a flat mid-grey | `surfacePropertiesShader` on the manager is empty, or the surfaces sit on a layer left out of **Lit surfaces**. |
| No beams | `volumetricShader` is empty, `volumetricIntensity` is 0, or quality is `Off`. |
| Beams show visible stepping | Set quality to `High`. Wide cones, long throws and dense haze show stepping before a narrow spot does. |
| Fixture bodies dark while the light they cast works | The manager publishes the DMX grid textures for the bodies. Check its texture slots, including the strobe timer. |
| DMX values plausible but wrong | Addressing. A fixture patched at the wrong start channel reads its neighbour's channels and still looks deliberate. Open the DMX Monitor: a patch off by one shears diagonally across the grid. Check that `use5ChannelMode` matches the patch. |
| A DMX fixture is half as bright as in an older version | Intended. `maxIntensity` is now on the same scale as a URP spot light's Intensity. Double it, or set `curveMod` to 1. |
| Beams missing in a mirror | The secondary-camera policy on the manager. `Match` lights mirrors like the main view. `Reduced` keeps beams at a lower level. `SurfaceOnly` and `Skip` remove them. |
| A fixture body vanishes when depth priming is on | Its shader lacks a depth pass that matches its forward pass. **Validate Renderer Setup** names any opaque shader in the scene that lacks one. Every shader this package ships has both. |
| Frame time worse than expected | In order: set quality to `Standard` or `Off`; set contact shadow strength to 0 if you do not need it; check that `lightCullShader` is assigned. Then run **Analyse This Scene**. |
| Par, blinder, laser or discoball bodies wrong in VR | Known issue, see `CHANGELOG.md`. Moving heads, surface lighting and beams are unaffected. |

## Where the DMX comes from

Two routes exist, and a scene can carry both.

- **A video grid.** A frame of video encodes channel values as colours. The package decodes it on the GPU, and the fixtures read the result. Any video source that ends up as a texture will do. This is how DMX reaches a world with no other way in.
- **Bytes.** Where the values already arrive as data, a channel source hands them straight to the manager. Nothing is encoded or decoded. [Truss](https://github.com/towneh/Truss) carries a desk's Art-Net inside a live H.264 stream. A Basis media player that shows that stream feeds the rig through **VRSL → URP → DMX Config → Add Basis DMX Record Source (SEI)**. The fixtures read the exact bytes the desk sent. Every record carries a checksum. No part of the picture is given up to a grid.

[DMX Channel Sources](https://github.com/towneh/vrsl-urp/wiki/DMX-Channel-Sources) covers both routes. It also covers how to frame a grid that arrives inside a larger picture, and how to write a source of your own.

## Migrating from Built-in

Both packages can be installed at once. Asset GUIDs, shader names, C# namespaces and DMX shader globals are all distinct between them. The shader names are `VRSL-URP/…` against `VRSL/…`, the namespaces `VRSL.URP` against `VRSL`, and the globals `_VRSLU_DMX*` against `_Udon_DMX*`.

The two run side by side without touching each other.

Two menu items appear when the original package is present:

- **VRSL → URP → Migrate Scene Fixtures (Add URP Siblings)** places the matching URP fixture beside each original at the same transform. It leaves the original in place, so you can compare the two before you delete the originals.
- **VRSL → URP → Convert Custom Fixtures In-Place (Component + Material)** swaps the component and material on fixtures you built yourself.

Both match on the source prefab, and you can run them again safely. If you remove the original package afterwards, fixtures that referenced it keep missing-script slots. The package cleans those up in memory whenever a scene opens, without marking the scene dirty.

## Attribution

Substantial portions of the shaders, prefabs, fixture meshes, textures, decode chain and authoring components derive from [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) by **AcChosen**, MIT-licensed. Full credit and copyright to AcChosen. The URP realtime light path and the standalone restructuring on top of that base are this fork's contribution. See `LICENSE.md` and `NOTICE.md` for the per-component breakdown.

## Licence

MIT. See `LICENSE.md`.
