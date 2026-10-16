# VRSL for URP

Stage lighting for Unity 6 worlds on the Universal Render Pipeline. Moving heads, washes, pars and blinders light the room the way real fixtures do, with beams in the air. A lighting desk drives them over DMX, or the music drives them through [AudioLink](https://github.com/llealloo/audiolink).

The package is a standalone fork of [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) by **AcChosen**, rebuilt for URP. The original targets the Built-in pipeline and VRChat. This one targets URP and any host. It installs beside the original rather than replacing it, so a world can move over one fixture at a time.

**Where to read next**

| You want to | Go to |
|---|---|
| Get a rig lit in ten minutes | [Your first rig](#your-first-rig) below |
| Step-by-step guides in plain language | The [wiki](https://github.com/towneh/vrsl-urp/wiki) |
| Every inspector field, for both data paths | [Fixture Configuration Reference](https://github.com/towneh/vrsl-urp/wiki/Fixture-Configuration-Reference) |
| DMX as bytes rather than as a video grid, from a Basis media player or over OSC from Truss | [DMX Channel Sources](https://github.com/towneh/vrsl-urp/wiki/DMX-Channel-Sources) |
| See every channel's value live | [DMX Monitor](https://github.com/towneh/vrsl-urp/wiki/DMX-Monitor) |
| Make a shader of your own react to the desk, through `Runtime/Shaders/VRSLDMX.hlsl` | [DMX in Your Own Shaders](https://github.com/towneh/vrsl-urp/wiki/DMX-in-Your-Own-Shaders) |
| How it works inside, and its limits | [Architecture](https://github.com/towneh/vrsl-urp/wiki/Architecture) |
| What changed, and known issues | [`CHANGELOG.md`](CHANGELOG.md) |
| Verify a change to the package | [`TESTING.md`](TESTING.md) |

## What it does

- Fixtures light floors, walls and avatars in each surface's own colour and gloss, with optional screen-space contact shadows. There's no shadow map per light, so many fixtures cost about what a few Unity lights would.
- Beams are raymarched, with gobos, haze and strobe, and sit correctly against geometry in front of and behind them.
- The package adds its render passes at runtime and never edits your URP asset or renderer, so it works where the renderer isn't yours to change.
- DMX arrives as a video grid or as bytes inside a live video stream, and AudioLink can drive a rig from the music with no desk.

## What you need

- Unity 6000.4 or newer
- Universal Render Pipeline 17.4 or newer, in Forward or Forward+ mode
- AudioLink 3.1.2 or newer, only to drive fixtures from music. Without it the DMX side works as normal, and the AudioLink fixtures, menus and example scene aren't usable until it's installed.
- A GPU with compute shaders, on Windows (D3D11 or D3D12, desktop and PC VR), Android and Quest (Vulkan, multiview) or desktop Linux (Vulkan). Headless Linux servers run it too. macOS and iOS aren't supported. Fixture body lighting hasn't been checked on a Quest headset yet.

## Install

In the Package Manager, choose **Add package from git URL** and paste:

```
https://github.com/towneh/vrsl-urp.git
```

Or, from a local clone, point `Packages/manifest.json` at the folder:

```json
"town.mr.vrsl-urp": "file:C:/path/to/vrsl-urp"
```

There's no renderer feature to add. After installing, run **VRSL → URP → Validate Renderer Setup** once. It reads your pipeline asset and the open scene, says whether anything needs to change, and changes nothing itself.

## Your first rig

1. **Open an example scene.**

   | Scene | Folder |
   |---|---|
   | `VRSL-ExampleScene-AudioLink-URPRealtimeLights` | `Runtime/Example Scenes/AudioLink-Scenes/` |
   | `VRSL-ExampleScene-EditorViaOSC-Horizontal-URPRealtimeLights` | `Runtime/Example Scenes/DMX-EditorViaOSCScenes/` |

   The AudioLink scene needs AudioLink and lights up as soon as audio plays. The DMX scene stays dark until a desk or DMX sender feeds it.

2. **Add a light manager to your scene.** Run **VRSL → URP → Add Light Manager to Active Scene**. Use one manager per scene for each data path. The manager arrives with every shader and texture reference filled in. Its inspector shows one status line that says whether it is wired. If a reference goes missing, run **VRSL → URP → Repair Manager Wiring**.

3. **Add fixtures.** Drag prefabs in from `Runtime/Prefabs/DMX/` or `Runtime/Prefabs/AudioLink/`. Or add `VRSLDMXRealtimeLight` or `VRSLAudioLinkRealtimeLight` to fixture geometry of your own. For AudioLink movers built on the original VRSL's mover hierarchy, **VRSL → URP → AudioLink Config → Setup AudioLink Realtime Lights in Scene** adds and wires the component; the shipped prefabs already have it.

4. **Patch them.** Give each DMX fixture the universe and start channel that match the desk. The prefabs use VRSL's 13-channel layout, and the five-channel statics have a layout of their own. An AudioLink fixture takes a frequency band and a colour source instead.

5. **Press play.** Surfaces should light and beams should appear. If they do not, select the manager, right-click its header and run **VRSL Diagnostics**, which says which stage went quiet.

The per-field detail for every fixture type is in the [Fixture Configuration Reference](https://github.com/towneh/vrsl-urp/wiki/Fixture-Configuration-Reference).

## The controls that matter

The controls that set the package's cost are on the manager. The artistic ones (density, tint, anisotropy, intensity, fog coupling) change the look at a fixed price.

| Control | Choices | What it decides |
|---|---|---|
| **Quality** | `Off`, `Standard`, `High` | How much of the frame the package may spend. `Standard` suits most worlds. `High` marches the beams more finely and traces contact shadows further. `Off` keeps surfaces lit and removes the beams and the shadows. Each level has a fixed cost, so a level costs the same in every scene. |
| **Secondary cameras** | `Match`, `Reduced`, `SurfaceOnly`, `Skip` | What mirrors, portals and camera props get; each runs the light path again. `Reduced`, the default, lights them one quality level below the scene, `SurfaceOnly` drops the beams, and `Skip` leaves them unlit by VRSL. |
| **Contact shadow strength** | 0 to 1 | Screen-space shadows from the geometry the camera can see. Off by default, because it is the most expensive term in the surface pass. |
| **Lit surfaces** | Layer mask | Which layers keep their own colour, gloss and normal maps when lit. A layer left out is still lit, as a plain mid-grey surface. Leave it at Everything unless a layer is expensive to draw. |

## Checking it works

| Tool | What it answers |
|---|---|
| **VRSL → URP → Validate Renderer Setup** | Which renderer each camera uses, its depth-priming and MSAA modes, and whether the prepass covers the layers your fixtures sit on. Also what each camera renders at under the secondary-camera policy, and any opaque shader that lacks a depth pass. It only reads. |
| **VRSL → URP → Validate Shaders** | Whether every package shader compiled. A full-screen shader that fails draws nothing rather than drawing wrong. Run this first when the scene is dark. |
| **VRSL Diagnostics** (right-click the manager in play mode) | Whether the decode produced data, whether the tile cull keeps it, whether the prepass feeds the surfaces, and what the beams took. |
| **VRSL → URP → DMX Config → DMX Monitor** | Every channel of a universe as a live grid, which of the two DMX paths the fixtures read, and how long ago each universe was last heard from. |
| **VRSL → URP → Performance → Analyse This Scene** | What the package costs in the open scene at each quality level, and whether it fits a refresh-rate budget. |

## Troubleshooting

Start with **VRSL Diagnostics** on the manager. From outside, most faults look like nothing being lit, and the report says which stage went quiet.

| Symptom | Likely cause |
|---|---|
| Nothing lit at all | Most often a full-screen shader that did not compile; **Validate Shaders** will say. |
| Everything lit a flat mid-grey | `surfacePropertiesShader` on the manager is empty, or the surfaces sit on a layer left out of **Lit surfaces**. |
| No beams | `volumetricShader` is empty, `volumetricIntensity` is 0, or quality is `Off`. |
| Beams show visible stepping | Set quality to `High`. Wide cones, long throws and dense haze show stepping before a narrow spot does. |
| Fixture bodies dark while the light they cast works | The bodies read the grid the manager publishes from its **Grid Textures** asset. Check it's set and the layout in use has every slot filled, including Strobe Timer. |
| DMX values plausible but wrong | Usually addressing. A fixture patched at the wrong start channel reads its neighbour's channels and still looks deliberate. Open the DMX Monitor: a patch off by one shears diagonally across the grid. Check that `use5ChannelMode` matches the patch. |
| A DMX fixture dimmer than expected | `maxIntensity` is on the same scale as a URP spot light's Intensity. Raise it, or set `curveMod` to 1. |
| Beams missing in a mirror | The manager's **Secondary cameras** setting (see [The controls that matter](#the-controls-that-matter)). |
| A fixture body vanishes when depth priming is on | Its shader lacks a depth pass that matches its forward pass. **Validate Renderer Setup** names any opaque shader in the scene that lacks one. Every shader this package ships has both. |
| Frame time worse than expected | In order: set quality to `Standard` or `Off`; set contact shadow strength to 0 if you do not need it; check that `lightCullShader` is assigned. Then run **Analyse This Scene**. |
| Lens flare, laser or discoball missing from one eye in VR | These three haven't been checked in a headset yet; please report one missing. Moving heads, fixture bodies, surface lighting and beams draw in both eyes. |

## Where the DMX comes from

Two routes exist, and a scene can carry both.

- **A video grid.** A frame of video encodes channel values as colours, the package decodes it on the GPU, and the fixtures read the result. Any video source that ends up as a texture will do, which is how DMX reaches a world with no other way in.
- **Bytes.** Where the values already arrive as data, a channel source hands them straight to the manager, with no encoding or decoding in between. [Truss](https://github.com/towneh/Truss) carries a desk's Art-Net inside a live H.264 stream, and a Basis media player showing that stream can feed the rig through **VRSL → URP → DMX Config → Add Basis DMX Record Source (SEI)**. The fixtures read the exact bytes the desk sent, and none of the picture is given up to a grid.

[DMX Channel Sources](https://github.com/towneh/vrsl-urp/wiki/DMX-Channel-Sources) covers both routes, along with how to frame a grid that arrives inside a larger picture and how to write a source of your own.

## Migrating from Built-in

Both packages can be installed together. Their shader names (`VRSL-URP/…` against `VRSL/…`), C# namespaces (`VRSL.URP` against `VRSL`), DMX shader globals (`_VRSLU_DMX*` against `_Udon_DMX*`) and asset GUIDs are all distinct.

With the original package installed, two menu items appear:

- **VRSL → URP → Migrate Scene Fixtures (Add URP Siblings)** places the matching URP fixture beside each original fixture prefab, its body where the original's is, and leaves the original so you can compare before deleting it. The summary names settings that didn't come across and what deleting the originals would break.
- **VRSL → URP → Convert Custom Fixtures In-Place (Component + Material)** swaps the component and materials on fixtures you built yourself, and moves references to the old component onto the new one.

Running either again skips fixtures already done. If you then remove the original package, the package strips leftover missing-script slots from VRSL fixtures in memory whenever a scene opens, without marking it dirty. [Migrating from Built-in](https://github.com/towneh/vrsl-urp/wiki/Migrating-from-Built-in) has the details.

## Attribution

Substantial portions of the shaders, prefabs, fixture meshes, textures, decode chain and authoring components derive from [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) by **AcChosen**, MIT-licensed, with full credit and copyright to AcChosen. The URP realtime light path and the standalone restructuring on top of that base are this fork's contribution. See `LICENSE.md` and `NOTICE.md` for the per-component breakdown.

## Licence

MIT. See `LICENSE.md`.
