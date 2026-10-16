# Testing VRSL URP

How changes to this package get verified. Pick the rows a change plausibly touches, run
those, and report results against them by name — "tested" without naming rows isn't a
verification claim.

Most of this needs eyes on a running scene. Two things don't and should run first, because
they're fast and they catch the failures that are hardest to read from the outside.

---

## Before anything else

### 1. Shader validation

`VRSL → URP → Validate Shaders`, or headless:

```
Unity.exe -batchmode -quit -projectPath <project> \
  -executeMethod VRSL.URP.EditorScripts.VRSL_ShaderValidation.ValidateFromCommandLine \
  -logFile -
```

Exits non-zero on any error. Run it after touching any `.shader`, `.hlsl` or `.compute`.

**Why it matters:** a fullscreen pass that fails to compile draws *nothing* rather than
drawing wrong, so a shader error presents as "VRSL lighting stopped working" with no visible
cause. The Console entry is easy to lose in a project's normal noise.

**What it doesn't cover:** editor shader compilation is lazy — Unity compiles the variants the
importer asks for, not the full keyword matrix. Errors confined to a variant only a specific
scene requests can still get through. It reliably catches base-variant failures, which
includes anything structural. It also compiles for the editor's own graphics API only; rows
T1 and T2 (**Platforms**) compile every shader for Android and Linux on Vulkan.

### 2. The DMX suite

Most rows below are PlayMode tests, and each section names its test classes. Run them
rather than performing them by hand:

```
Unity.exe -batchmode -projectPath <project> -runTests -testPlatform PlayMode \
  -assemblyNames "Towneh.VRSL.URP.Tests;Towneh.VRSL.URP.Basis.Tests;Towneh.VRSL.URP.AudioLink.Tests" \
  -testResults results.xml -logFile -
```

or from the Test Runner window, assembly `Towneh.VRSL.URP.Tests`. Headless needs the editor
closed and a real GPU, and the project's `Packages/manifest.json` has to list
`town.mr.vrsl-urp` under `testables`, or the run exits 0 having run nothing. The suite builds
its own rig in code, so it needs neither the profiling sample nor a hand-authored scene.

The **Measurement harness** rows (H1-H9) are in the same assembly and run the same way. Most
measure rather than decode, so they are the slow part of a full run; H7 is a plain unit row
and costs nothing, as are H8 and H9.

The **Wiring** rows (W1-W9), the **Image regression** ones and the **Cameras** rows M5 and
M7 to M9 (`VRSLCameraPolicyTests`) are in it too. The wiring
rows are editor-only and cost nothing. W10 to W12 are hand rows and are marked as such:
three are judged by looking at an inspector (W10, W11 and W11b), and W12 is a player
comparison (see **The headless gate**).

A second assembly, `Towneh.VRSL.URP.Basis.Tests`, exists only when `com.basis.mediaplayer` is
in the project and holds the Basis integration rows (B7 to B11, N23 to N26, E8 and E9). Those
play real streams: the Truss fixtures (`truss-dmx-ramp.ts` and its damaged, stripped and
transcoded variants, and `vrsl-dmx-marching.ts`). `VRSL_TRUSS_FIXTURES`, a directory or a URL
base, says where they are; without it the rows look under `https://www.mr.town/vod/`. A
fixture the rows can't fetch fails the row on the download, so check the source serves them
before reading a failure as a regression.

A third, `Towneh.VRSL.URP.AudioLink.Tests`, exists only when `com.llealloo.audiolink` is in the
project, like the AudioLink assemblies it tests. It holds the AudioLink halves of H8, Q5, C9,
C10, the AudioLink wiring check and the **AudioLink (optional)** rows Y4 to Y6.

A fourth, `Towneh.VRSL.URP.Basis.XR.Tests`, exists only when `com.unity.xr.mock-hmd` is in the
project and holds the suite half of X4. It renders through Unity's Mock HMD in single-pass
instanced stereo on Basis's own desktop and Android pipeline assets, and ignores itself where
they aren't at `Assets/Basis/Settings/Quality Settiings/`. It needs the editor window: in batch
mode Mock HMD gets a 0x0 eye texture and nothing renders, so the row ignores itself there. Run
it on its own without `-batchmode`; the editor opens, runs it and quits:

```
Unity.exe -projectPath <project> -runTests -testPlatform PlayMode \
  -assemblyNames "Towneh.VRSL.URP.Basis.XR.Tests" -testResults results.xml -logFile -
```

Importing Mock HMD writes its loader and settings assets under the project's `Assets/XR/`;
nothing references them.

The rows are written out in full below, because they are the specification the tests
implement and because a failure message is only useful next to the claim it belongs to. Do
them by hand when you need to see something on screen, or when a test fails and you want to
watch what it was looking at.

**`-nographics` is not an option.** What is under test is compute kernels, so the run needs
a graphics device; a null device fails on the first dispatch.

**The suite captures time rather than measuring it.** `Time.captureDeltaTime` makes every
frame advance a fixed slice of game time however fast the machine renders, so a wait is a
frame count and a run is reproducible. The movement rows need that: judged against
wall-clock they are repeatable to a second or two at best, which is coarser than the
effects they exist to separate.

### 3. Diagnostics

Right-click either light manager in play mode → **VRSL Diagnostics**. Reports shader compile
state, decoded light data, tile-cull statistics, prepass configuration and camera mode.

**Play mode only.** Neither manager has `[ExecuteAlways]`, so nothing initialises in the
editor — the report says so rather than presenting empty figures as findings.

Run this first whenever something is dark. It separates causes that look identical on screen:

| Report line | Means |
|---|---|
| `FAILED TO COMPILE` | Shader problem. Nothing downstream matters until it's fixed. |
| `Light data: 0/N emitting` | Data problem — the decode produced nothing. Rendering is irrelevant. |
| `Tile culling: INACTIVE` | Falling back to iterating every fixture. Correct, but unbounded in cost. |
| `want more than the ...-light cap` | Fixtures are being dropped in dense tiles, and the note says how much light was lost. |
| `Surface prepass: normals only` | Everything lights as neutral grey; albedo isn't reaching the BRDF. |
| `NOT IN PLAY MODE` | Nothing is initialised. Enter play mode; shader assignment is still reported. |
| `Fixtures: NONE FOUND` | The manager collected nothing on enable — wrong manager for the scene, or fixtures inactive/added since. |

### 4. The DMX monitor

`VRSL → URP → DMX Config → DMX Monitor`. Every channel of a universe as a cell shaded by value, live,
from whichever source is driving the scene. Read-only — it never writes a channel.

**Play mode only**, for the same reason as the diagnostics report.

Reach for it before the report when the question is about the *data* rather than about
the rendering. It separates causes the lights cannot:

| What you see | Means |
|---|---|
| `Reading` shows `Stream data — <source>` | Fixtures are reading published bytes, not the grid |
| `Reading` shows `Stream data — nothing is sending it, lights dark` | DMX Input is Stream data and no source is publishing, or the one attached publishes no universes; the warning under it says which. The lights are dark, and nothing falls back to a grid |
| `Reading` shows `Video grid — <layout>` | Fixtures are reading that layout's grid; a source named in brackets is on standby |
| `Change` ramp completely dark | No channel is *moving*. Not a liveness test on its own — a source republishing unchanged values reads the same as one that stopped. Confirm against `last heard N s ago` |
| `last heard N s ago` climbing | That universe has stopped arriving; its last values are still on screen |
| Diagonal shear in `Fixture` view | The patch is offset. One row is one 13-channel fixture, so an off-by-one is visible as a shear rather than as plausible colour |
| Non-zero in a padding cell | A stride fault in the manager's scatter, or something writing the flat space outside it. **Not** an overrunning block — `ScatterBlocks` clamps every run to the universe's usable slots, so a block cannot reach these addresses however it is malformed |

**What it doesn't cover:** on the video path it decodes through the compute shader in the
manager's grid layout, so a nine-universe grid does not read correctly there. The window
states this whenever it is on the video path. The buffer path is unaffected.

---
## Test matrix

Rows are independent. `D` = DMX path, `A` = AudioLink path, `—` = either.

### Core light path

| # | Path | Scenario | Expected |
|---|---|---|---|
| C1 | D | Open a DMX example scene, run a cue | Fixtures track dimmer, colour and strobe; `Log Decoded Fixture Light Data` shows non-zero intensity on lit fixtures |
| C2 | D | Patch a fixture at a channel whose offset lands mid-row (base ≡ 1 mod 13, read +5 dimmer) | Dimmer responds to the correct channel, not a neighbour's. **Guards the decode row truncation** |
| C3 | D | Moving head, pan and tilt channels | Beam aims correctly; fine channels smooth the motion |
| C4 | A | AudioLink scene with audio playing | Fixtures react to their band; `bandMultiplier` changes sensitivity, not peak |
| C5 | — | Fixture faded to black with dimmer up | Light fully extinguishes, no residual glow |
| C6 | — | `VRSLManagerLifecycleTests` in the suite: a manager switched off in the inspector loads beside a running one | The running one owns the singleton and drives the light path. A switched-off component still gets `Awake` but never `OnEnable` or `OnDisable`, so a claim made there would never be released and the running manager would destroy itself as a duplicate: a scene with a manager in it, no lighting, and nothing in the Console |
| C7 | — | `VRSLManagerLifecycleTests` in the suite: two managers, one of them switched on after the other already owns the singleton, then the owner switched off, with a channel source attached | The one still running takes the singleton over, the channel source comes with it, and the light path keeps going. A manager that stood down on being enabled gets no second `OnEnable`, so without a handover the scene is left with an enabled manager, no owner and no lighting. The source has to travel too, or the new owner stops publishing on its first frame |
| C8 | — | `VRSLManagerLifecycleTests` in the suite: a second manager left switched on beside the one that owns the singleton | The owner's channel-count global survives. Per-frame work is the owner's alone: a non-owner reaching `UploadChannels` would rewrite the shader globals, and `AdvanceState` would set parameters on the compute asset both managers share. The row reads the global because it is cheap and unambiguous; the shared compute asset has no row of its own |
| C9 | A | `VRSLAudioLinkFixtureTests.C9` (AudioLink test assembly): an AudioLink realtime light with a body renderer in each theme-colour mode | The body's property block carries theme colour 1 for ThemeColor0 up to 4 for ThemeColor3, the numbering the body shader reads (anything else draws black) |
| C10 | A | `VRSLAudioLinkFixtureTests.C10`: the AudioLink manager holding one fixture, the fixture destroyed with no `RefreshFixtures()` after it, then the per-frame upload | No exception, and the slot uploads dark with a direction the compute can normalise. An exception there is thrown inside camera rendering and stops every AudioLink pass |
| C11 | — | `VRSLManagerLifecycleTests.C11`, and `VRSLAudioLinkLifecycleTests.C11_AudioLink` in the AudioLink test assembly: a manager with fixtures switched off, then on | The lighting and volumetric materials it made are destroyed when it is switched off, and it makes new ones when it is switched back on. They are made HideAndDontSave, which a scene unload doesn't collect, so any that outlive their manager stay for the rest of the session |
| C12 | — | `VRSLManagerLifecycleTests.C12` and `VRSLAudioLinkLifecycleTests.C12_AudioLink`: the manager switched off, given another lighting shader and switched back on; switched off, set to quality Off and switched on; then set to Standard while it runs | It draws with the new shader. At Off it holds no volumetric material; set to Standard while running it makes one, so beams appear without switching the manager off and on |
| C13 | D | `VRSLManagerLifecycleTests.C13`: a manager with the synthetic channel source attached, switched off with no other manager to take over, the source destroyed, the manager switched back on | The manager lets go of the destroyed source within a frame. A source that detaches through `Instance` finds nobody there while its manager is off, and held as the interface the destroyed source doesn't compare null, so the manager would go on reading it every frame |

### Surface accuracy

`VRSLSurfacePrepassTests` in the suite covers S10 to S21. S1 to S9 are judged by eye.

**Where the normals come from decides which passes are in the frame.** Wherever URP's
own depth-normals prepass can be drawn, the manager asks URP for it and VRSL's normals
draw does not run: a frame capture then shows URP's `DepthNormals` prepass and no
`VRSL Normals Prepass`, and the lighting is the same to the pixel (S11). URP cannot
draw it on a camera that is both multisampled and depth primed, and on a URP that
primes with MSAA the request itself fails the whole frame (S13), so there VRSL draws
its own. `VRSL → URP → Validate Renderer Setup` says which each camera will get and why;
`VRSL Diagnostics` on a manager says which the last camera got.

**The GPU Resident Drawer takes its batches out of the albedo capture.** The drawer draws
them with each material's own shader and ignores `DrawingSettings.overrideShader`, so the
prepass leaves its batch layers out and those meshes light as the neutral fallback (S15).
Basis ships the drawer on for desktop, so on that configuration every Lit-family mesh it
batches lights mid-grey; avatar shaders without DOTS instancing are not batched and keep
their colour. `VRSL Diagnostics` and `Validate Renderer Setup` say when the drawer is on.

| # | Path | Scenario | Expected |
|---|---|---|---|
| S1 | — | Spot onto a strongly-coloured textured surface (URP Lit) | Surface keeps its hue; it does **not** wash towards white |
| S2 | — | Spot onto an avatar (Poiyomi / lilToon URP) | Lit, and keeps its texture colour. Black or double-darkened means the override-shader property resolution needs instrumenting |
| S3 | — | Spot across a glossy floor, camera moving | Specular highlight tracks the viewer |
| S4 | — | Metallic vs dielectric material side by side | Metal tints its specular and loses diffuse |
| S5 | — | Geometry drawn by a shader with no forward LightMode tag | Lights as neutral mid-grey rather than black |
| S6 | — | Unassign `surfacePropertiesShader` | Everything falls back to neutral grey, nothing goes black |
| S7 | — | Match `maxIntensity` against a URP spot light of the same Intensity | Comparable brightness at full output |
| S8 | — | Avatar using Poiyomi UV Tile Discard, both `Vertex` and `Pixel` discard modes, standing in a beam | Discarded regions stay invisible. **In `Vertex` mode the prepass draws geometry the camera dropped, so the surface behind must not pick up the avatar's albedo** |
| S9 | — | Avatar whose shader displaces vertices, standing in a beam | Lit without a mismatched-colour ghost offset from the mesh. Neutral grey there is the expected fallback, not a bug |
| S10 | — | **Suite.** The rig at MSAA 1 and MSAA 4 with priming Disabled, where URP's normals are read at both, and a third capture at 4x with `forceOwnNormals` | Surface shading matches across MSAA: the lit area within half a point and the mean lit colour within 2 of 255, since multisampling moves every edge and pixels are not compared. The two 4x captures, which differ only in where the normals came from, agree to the pixel. Priming is Disabled so the 4x frame isn't S14's fault |
| S11 | — | **Suite.** MSAA 1, priming Forced, the policy left to decide and then overridden with `forceOwnNormals` | With the policy, the prepass reads URP's texture on every render and draws no normals of its own; forced, the reverse; and the two frames agree. The hand half: a frame capture with the policy engaged shows no `VRSL Normals Prepass` and URP's `DepthNormals` prepass in place of its `DepthOnly` one |
| S12 | — | **Suite.** The rig's floor moved to a layer left out of `prepassLayers`, against the same mask leaving out a layer nothing is on | The floor still lights, as neutral grey rather than black, geometry inside the mask is untouched, and leaving out the empty layer changes nothing |
| S13 | — | **Suite.** URP's `_CameraNormalsTexture` beside `_VRSLNormalsTexture` in the same frame, at priming Forced and Disabled, each at MSAA 1 and 2 | Identical wherever URP can draw it. The row reads whether URP actually primed (the renderer's internal `useDepthPriming`, by reflection): where it primed at MSAA 2 the frame fails with a Render Graph error, which is why the policy refuses MSAA with any priming but Disabled; where URP declined to prime, as stock URP does for a multisampled camera, that frame must match like priming Disabled. The row asks for MSAA on the rig's target and holds the pipeline asset to it, since a camera rendering into a texture takes its sample count from the texture |
| S14 | — | **Suite.** Volumetrics off, priming Forced: the floor lit at MSAA 1 and at MSAA 2, the sample count Basis ships, against the 2x frame with the floor left out of the prepass | The floor shades at 2x as it does at 1x, not as the neutral fallback. **`[Ignore]`d in the suite, naming the fault**: under priming with MSAA, `_CameraDepthTexture` is a resolve that takes the farthest sample, at a grazing angle centimetres from the depth the prepass rasterised, and `VRSL_SurfaceDataCovers` allows 0.05% of eye depth, so it rejects the surface and the lighting falls back to mid-grey. Remove the attribute to run it. A change to the gate wants S8 and S9 re-run on avatars |
| S15 | — | **Suite.** The rig's floor on a URP Lit material of known colour, gloss and metallic, captured through the prepass targets with the GPU Resident Drawer Disabled and then on, the mode held on the pipeline asset per render | Disabled, the prepass reads the material back as authored. On, the floor is not captured at all and the frame still lights it, as the neutral fallback. Inconclusive if the drawer did not come up with the mode set, naming the project's reason |
| S16 | — | **Suite.** The rig's floor on URP Lit with a one-colour metallic map (metallic in red, smoothness in alpha) and scalars that disagree with it, captured three ways: the map keyword on; the keyword off with the map still assigned; the keyword on with smoothness taken from the base map's alpha | Keyword on: metallic from the map's red channel, smoothness from its alpha times `_Smoothness`. Keyword off: the scalars, as URP Lit itself reads them. Albedo-alpha mode: the base texture's alpha times `_Smoothness`. The keyword-off capture separates "read the keyword" from "always read the map", and the row also shows a material's keywords reach an override-shader draw |
| S17 | — | **Suite.** The rig's floor on URP Lit of known colour, gloss and metallic, captured through the prepass targets with the pipeline asset's render scale held at 1, 0.5 and 1.5 | At every scale the prepass captures at least 95% of the floor and reads its material back as authored. Inconclusive if the camera's target didn't come back at the scaled width, since then nothing scaled |
| S18 | — | **Suite.** The rig's floor tilted off level (a component on every axis) on URP Lit and then on each fixture body shader (Opaque, Opaque Multi-Channel Bar, the AudioLink static fixture), all with a flat normal map, each captured twice: with Force own normals on, so VRSL's own draw fills `_VRSLNormalsTexture`, and with URP asked for `_CameraNormalsTexture` | The forced capture ran VRSL's own draw and read nothing from URP. In both textures, on every body shader the floor reads as its own world-space normal over at least 90% of the pixels it does on URP Lit, and loses no more than a tenth of the floor's written pixels. Their `DepthNormals` pass writes the signed world-space normal URP's own passes write; a view-space, 0..1-packed or fixed-direction normal leaves almost none. Inconclusive for URP's texture if URP drew no normals for the Lit floor |
| S19 | — | **Suite.** S18's tilted floor on URP Lit and then on each fixture body shader, nothing assigned to the normal map, Force own normals on | On every body shader the floor in `_VRSLNormalsTexture` reads as its own world-space normal over at least 90% of the pixels it does on URP Lit, and each shader's `_NormalMap` defaults to `bump`. A default that doesn't unpack to a flat normal tilts the floor off its normal |
| S20 | — | **Suite.** S18's tilted floor with black vertex colours on URP Lit and then on each mover body shader (DMX and AudioLink, spot and wash), captured with Force own normals on and with URP asked for `_CameraNormalsTexture`; then each mover again with purple vertex colours (red and blue at 0.5), which the body discards and the movers' pan, tilt and projection scaling leave in place | The forced captures ran VRSL's own draw and read nothing from URP. In both normals textures the floor reads as its own world-space normal over at least 90% of the pixels it does on URP Lit, and loses no more than a tenth of the written pixels; the camera's depth texture, which under the rig's depth priming comes from the `DepthOnly` pass, holds the floor. With purple vertices the floor's normal covers no more than 1% of what it did, and the camera's depth loses at least 90% of the floor. Inconclusive for URP's texture if URP drew no normals for the Lit floor |
| S21 | — | **Suite.** The floor on URP Lit with culling off, captured as shipped and with its triangles wound the other way (the camera sees its back face, normals still up); then wound the other way with culling back on | Seen from either side, the double-sided floor is captured over at least 95% of its pixels with its colour, smoothness and metallic. Seen from behind, the single-sided floor holds no more than 1% of the normals-texture pixels the double-sided one does. In all three captures the prepass writes depth at no pixel where the camera's depth texture holds nothing |

### Occlusion

| # | Path | Scenario | Expected |
|---|---|---|---|
| O1 | — | `contactShadowStrength` 0 → 1, avatar standing in a beam | Shadow appears at its feet |
| O2 | — | Walk the occluder towards the edge of frame | Shadow fades out as it leaves screen. **Expected** — screen-space only |
| O3 | — | Wall between fixture and a surface across the room | Still lit through the wall. **Expected** — no light-perspective shadows |
| O4 | — | `Standard` against `High` on thin geometry | Less light leaks through at `High`, which traces further and samples more finely. The sample count is the level's, not a field |
| O5 | — | `VRSLLightStencilTests`: the DMX and AudioLink lasers (on their prefab's mesh) and blinder lens flares (on a quad), each on a copy of its shipped material, lit red with no DMX or AudioLink input; captured shown and hidden without and then with a test shader writing stencil 142 over the whole view (colour and depth untouched, early in the opaque queue, as the original VRSL's light stencil did), depth priming off; measured on a half-float capture, summed over the frame. A probe drawing only where the stencil holds 142 shows the write reached the frame, or the row is inconclusive | Each adds the same light with the stencil as without, within 2%. The package has no light stencil, so nothing a world writes to the stencil hides a light |
| O6 | — | `VRSLLightStencilTests`: the rig's frozen frame (every dimmer up, beams aimed at the floor) at Standard and at Off, without and then with O5's writer, then with the manager off; depth priming off | At each level the frame is the same with the stencil as without, within the image rows' 1.5/255. Standard against Off and Off against the manager off each differ on at least 1% of the frame, or the row is inconclusive: there'd be no beams or no surface light to judge |

### Cost and culling

| # | Path | Scenario | Expected |
|---|---|---|---|
| P1 | — | Clear `lightCullShader`, compare | Identical image, worse frametime. Any visual change means the cull is wrong. The pixel half of this claim is the P1 row in **Image regression**, which is the same P1 — one claim, measured two ways |
| P2 | — | Diagnostics with fixtures spread across the venue | Average lights/tile well below fixture count |
| P3 | — | Profiling sample sweep, 10 → 25 → 50 → 100 → 200 | Frametime scales sub-linearly with fixture count |
| P4 | — | `InsideCones` vs `OutsideCones` camera variants | Inside is the worst case; the gap shows culling working |
| P11 | — | A scene dense enough that a tile wants more fixtures than the per-tile cap allows. Read the tile figures from `VRSL Diagnostics` or a sweep row | Lights per tile reports what the tile **asked for**, above the cap, not the cap itself. The dropped figure is non-zero and says how much light the scene is not drawing. A tile exactly at the cap reports zero dropped, so the two cases are distinguishable |
| P12 | — | The same scene through `VRSL → URP → Performance → Analyse This Scene`. **Judged by reading it as a world author would**, not by grepping for a string | The summary says parts of the scene are missing light they should be getting, how many fixtures reach the same part of the screen against how many are lit, and what to change: spread them out, aim them elsewhere, or use fewer. It names no tiles and no cap: those describe the cull, and an author cannot act on either. It must not claim whole fixtures are dark — a fixture dropped from a busy tile still lights the tiles it wins, so what is lost is contributions rather than fixtures |

**Read the tile figures with the camera beside them.** One cull pass serves every
camera in the frame and each record overwrites the last, so a readback taken after the
frame describes whichever camera rendered last — which in a host project with its own
rig is not always the one a row is labelled with. Sweep rows carry `tileCamera` and the
tile grid for this reason; a figure quoted without them is a figure of an unknown view.

### Quality

`VRSLQualityPresetTests` in the suite covers Q3 and Q4, and `VRSLAudioLinkQualityPresetTests`
(the AudioLink test assembly) Q5. Q1 and Q2 want a frame capture,
and Q6 is judged by looking at the inspector.

| # | Path | Scenario | Expected |
|---|---|---|---|
| Q1 | — | A lit scene at each of `Off`, `Standard` and `High` | Beams in the air at `Standard` and `High` and none at `Off`; surfaces still lit at all three. `High` marches more finely than `Standard`, which is visible on a slow pan across a cone edge and not much else |
| Q2 | — | `quality = Off`, frame capture | No volumetric render targets allocated and no volumetric pass recorded. Judged in the capture: a pass that runs and draws nothing looks identical on screen |
| Q3 | — | `contactShadowStrength = 0`, and any strength at `quality = Off` | The trace does not run. The packed step count is 0, which the shader reads as skip — tracing and scaling the result to nothing is the most expensive term in the lighting loop spent for no picture |
| Q4 | — | `VRSLQualityLevel.For(Standard)` against the defaults the package shipped | Identical: 24 steps, 8 contact steps, 1.5 m, 0.5 m, noise on at 0.3 / 0.1 / 0.7. A scene authored before the quality field opens at `Standard` and costs what it did. An unrecognised level falls back to `Standard` rather than to something that draws nothing |
| Q5 | — | Both managers at each level | The same step, density, contact and noise parameters from each. A scene carrying both paths must not march at two budgets depending on which manager owns the pass |
| Q6 | — | Both manager inspectors, **judged by reading them as a world author would** | One quality control, then the look controls it governs. At `Off` those are greyed out rather than gone, with a line saying why — an author who forgot they switched it off should see the controls sitting there, not conclude the package is broken. No numeric cost knob anywhere, and no advanced foldout hiding one |
| Q7 | — | The control panel prefab dropped into a scene with a manager at `High`, enter play | Nothing changes: the beams march at `High`, the High button reads as selected, every slider sits at full and the light and beams are at what the scene was authored to. The panel is an overlay for the local user, not a second owner of the scene's settings |
| Q8 | — | Same, press Standard then Off, then drag the light and beams sliders | The manager's `quality` follows the buttons (both managers where the scene has both). The beams slider scales `volumetricIntensity` and the light slider scales `lightIntensity`, both as a share of the authored value, so a scene authored at 2 reaches 1 at half. Removing the panel from the scene changes nothing |
| Q9 | — | `lightIntensity` at 0.5 on the manager, no panel | Every fixture casts half the light and the beams dim with it; the fixture bodies glow as before, since the body glow is the material's and not the manager's |

### Wiring

`VRSLWiringTests` in the suite. These are about the assets a light manager points at —
the computes, shaders and decode textures it needs before it can light anything.
A manager whose fields are empty comes up dark and reports nothing wrong: the passes
run, the counters look healthy, and nothing appears on screen.

Six of those fields are on the DMX manager and five on the AudioLink one. On the DMX
manager the decode textures are one field, `gridTextures`, a Grid Textures asset holding
a set for each grid layout. **Three of the six are mandatory and fill themselves in; three
are offered rather than forced.** `lightCullShader`, `surfacePropertiesShader` and
`volumetricShader` each document a meaning when empty — no tile culling, no surface
prepass, no beams — and quality `Off` works by emptying the last of them. Resolution that
filled all six would take three documented capabilities away and make a deliberate clear
impossible, so enable and validate fill the mandatory three, and the repair button and the
menu item fill everything, because that is somebody asking.

| # | Path | Scenario | Expected |
|---|---|---|---|
| W1 | — | Every GUID in the wiring table, loaded and its path checked | All seven resolve, and every one to a path inside this package. Seven rather than eleven because the two managers share four assets between them and the table is walked by asset identity. A GUID that has drifted onto the legacy package's identically-named copy loads perfectly well and is the exact fault the table exists to prevent. Reported together rather than one at a time, so a GUID regeneration reads as a table that no longer describes anything rather than as one broken field |
| W2 | — | Every field name in the table, against the manager it names | Present on the manager. The table addresses fields by name, so a rename compiles cleanly everywhere else and stops resolution dead without saying so |
| W3 | — | Resolve a manager with one field deliberately pointed at the wrong asset and the rest empty | The override is untouched and is not reported as filled; every mandatory field is filled exactly once; nothing is unresolvable. Then `includeOptional` fills the remaining three, which is what the repair button does, and a second pass changes nothing — a repair pressed twice must not report changes it did not make |
| W4 | — | Two nested holds on auto-resolution, the inner one released first | The outer hold survives. Held as a set, not a count: the inner scope releasing the outer one's hold would let the next bounce refill a field the outer holder emptied on purpose. Disposing the same hold twice must not drop somebody else's |
| W5 | — | Two managers, the owner switched off so ownership passes to an unwired standby | The standby's mandatory fields are filled by the time it sets itself up. **The handover path never goes through `OnEnable`**: an owner standing down calls the successor's `TakeOwnership` directly, so a resolver hooked only there leaves this manager building its materials and passes from empty fields |
| W6 | — | Empty every field through `SerializedObject`, then run the validate pass | Still empty immediately afterwards, filled on the next editor tick. Resolution is deferred, because `OnValidate` is also where import and mid-edit serialisation live. The tick is driven rather than waited for: `EditorApplication.delayCall` does not fire inside a batch-mode play-mode test |
| W7 | — | An override on one field, the rest empty, through the enable path and then the deferred validate path | The override survives both, and each path fills what was empty. **Asserted after each path rather than once at the end**, so each path is shown to do its own work |
| W8 | — | A decoy asset with the same name as a wiring target, elsewhere in the project | Resolution picks this package's copy, by asset identity rather than by name. The row checks the decoy genuinely collides before it judges anything: if a name search is not ambiguous, resolution had nothing to get wrong and passing means nothing. It stages the collision rather than gating on `com.acchosen.vr-stage-lighting` being installed, because a gated row in a project without that package compiles out and proves nothing |
| W9 | — | Every consequence string in the table, once per distinct asset | Names what the author will see, not what is null. A consequence that repeats its own field name, or that talks about being null, is the kind of message the table exists to replace |

**W10 to W12 are hand rows.** W10, W11 and W11b are judged by reading the rendered
inspector, not by the message existing, since wording faults (a warning icon, a plural, a
paragraph naming a feature half its readers do not have) don't show in the source. W12 is a
player comparison, and the suite runs in the editor.

**Select the shipped prefab, and clear fields on the prefab asset rather than on a scene
instance.** Auto-resolution skips persistent objects, so a field cleared on an instance is
filled straight back in on the next editor tick and the warning branch never appears long
enough to read. A bare `AddComponent` is a different state again: the mandatory ones fill
themselves and the optional ones do not, so it lands in W10's second half rather than its
first.

| # | Path | Scenario | Expected |
|---|---|---|---|
| W10 | — | Select the shipped manager prefab asset and look at its inspector, then clear one of the optional fields | One line reading `Set up and ready.` and a fold closed by default, labelled `Assets this uses, and which ones fill in automatically`; opening it reaches every field, so an author who wants to point one somewhere else on purpose still can. The fold's paragraph quotes no count and names no field, since one fold serves both managers. Clearing an optional field must give a **plain grey sentence** naming what is currently switched off, and a **Turn these on** button — no warning box, no icon. An empty optional field is a choice |
| W11 | — | A mandatory field cleared, then a field whose asset is genuinely gone from the project | The first: a warning naming what you will see rather than what is missing (`Nothing lights from the video at all (gridTextures)`), and a **Set these up for me** button that works. A list, never a count. The second cannot be repaired and must say so in the same register, ending `Looked for an asset that is not in this project any more`. Stage the second by moving `VRSLDMXLightUpdate.compute` and its `.meta` out of the package (its only referrers are the manager prefabs); moving both files keeps the GUID, so putting them back restores it exactly |
| W11b | — | Press the repair button on a manager whose wiring **can** be filled | **No modal dialog.** `EditorUtility.DisplayDialog` draws a yellow warning icon whatever the message says, so a clean repair logs to the Console and lets the panel flipping to `Set up and ready.` be the acknowledgement. The dialog is kept for a repair that could not finish, where the icon is right, and for nothing being selected |
| W12 | — | The player sweep before and after a wiring change, compared against a floor measured on the same machine | **Every row unchanged.** Everything the wiring adds to the runtime assembly is inside `#if UNITY_EDITOR`, so none of it exists in a player. Derive the floor on the machine that judges the comparison; `Compare` refuses a reference from another GPU |
| W13 | — | Each of the fifteen slots in the shipped Grid Textures asset, against the GUID of the texture that layout ships | Every slot holds its own layout's texture, the Vertical and Legacy strobe timers included: a strobe timer from another layout would take its phase from a grid nothing writes to, and look just as healthy |

### Grid layouts

`VRSLGridLayoutTests`, `VRSLGridDecodeTests` and `VRSLGridSwitchTests` in the suite. The DMX manager reads one of three grid layouts,
Horizontal, Vertical or Legacy, from its DMX Input setting (`dmxInput`), and takes that layout's
decode textures from a Grid Textures asset. A manager saved with 0.2.0-rc.6 named its five
decode textures directly instead; its main texture survives in a hidden field, and the
upgrade reads the layout off it. Without the upgrade a Vertical or Legacy world comes up on
the Horizontal grid and lights the wrong fixtures with nothing in the Console. It runs on
the enable path, the deferred validate path and a scene build step, which Unity also calls
for scenes built into AssetBundles.

| # | Path | Scenario | Expected |
|---|---|---|---|
| G1 | — | A switched-off manager staged with an rc.6 main texture through the hidden field, once per shipped layout, brought across by each of the enable path, the deferred validate path and the scene build step | Comes up in the layout its texture belonged to, with the hidden field cleared and its live main texture that layout's from the shipped asset. Nine cases, because a path that only handled Horizontal would pass the Horizontal ones by doing nothing |
| G2 | — | The same with a texture that is none of the package's, on each path; then the manager given a Grid Textures asset of its own | Stays Horizontal, keeps the old texture and warns that it needs a Grid Textures asset made from its textures. Once it has an asset that is not the package's, the next upgrade clears the field and says nothing more. Guessing a layout from an author's own texture would be wrong as often as right, and dropping the reference would lose what the author needs to build the asset |
| G3 | — | **Hand row.** Select a DMX manager, then one carrying an rc.6 main texture of the author's own | **DMX Input** is the first control in the cost group, directly above Quality, offering Horizontal grid, Vertical grid, Legacy grid and Stream data, and its tooltip says what each reads, that the wrong grid lights the wrong fixtures, that Stream data needs a source and is dark without one, that a grid takes a few frames to settle, and that `SetDmxInput` takes the same choices as numbers. Under it: a source in the scene on standby under a grid ("… is on standby. Choose Stream data to read it."); under Stream data the source it reads ("Reads …"), or a warning that there is none and the lights will be dark. The **Asset references** fold shows Grid Textures and a line naming the layout it is read for, or that it isn't read under Stream data. The rc.6 manager also shows a warning naming its old texture and how to make a Grid Textures asset from it; the warning goes once one of the author's own is assigned, and the old reference is cleared. The AudioLink manager's inspector has no DMX Input |
| G4 | — | All fifteen of the shipped Grid Textures' CRTs set to Realtime, then a manager brought up at each layout | Only that layout's five are Realtime, the other ten are OnDemand, and `_VRSLU_DMXGridLayout` holds the layout. Staged Realtime first because the package ships every CRT OnDemand, so in a clean project the row would pass by doing nothing. **Switch half**: staged the same way, a manager on Horizontal switched to Vertical by `SetDmxInput` and then to Legacy by writing `dmxInput` (what the inspector or an Undo does in Play mode) runs only the new layout's five each time, and the global follows. A field written and the call made in the same frame binds at once rather than waiting for `LateUpdate`; a field written and then the call asking for the layout already bound puts the field back, and nothing switches. Once the manager lets go all fifteen are back at Realtime as staged |
| G5 | — | The fifteen CRTs staged at a mix of Realtime, OnDemand and OnLoad; a Vertical manager owns, hands over to a Legacy one, which is then switched off | Every CRT is back at its staged mode, the layout global read Vertical, then Legacy, then 0 (Horizontal) once nobody owns the singleton. **The order is the claim**: a manager puts its modes back before it hands over, so its successor notes the assets as they really are. In the editor a mode set on a CRT asset is written to the asset, which is why Play mode has to leave them as it found them |
| G6 | — | The rig's ramp through the channel buffer, then the same values on a painted 50 by 50 Legacy grid with the source detached: every channel the rig patches read back through the compute, and the fixtures' light | The compute reads every channel from the texel the shader-driven fixtures' include reads it from. Those texels are found on the GPU (`VRSLDMXRig.CellsOf`: the include's own cell position, sampled against a texture whose texels hold their coordinates), not re-derived, so a position on a texel edge lands where the sampler puts it. Every channel with a texel of its own decodes what the buffer holds, and every fixture lights the same colour and intensity as from the buffer. On the 50 by 50 textures a sector's 13th and the next sector's 1st sit a texel apart on texel edges and can settle onto one texel, differently on different GPUs, so the row judges channels that share a texel only against what the fixtures read |
| G7 | — | The rig brought up again with no Grid Textures and resolution held off, so nothing fills them, a channel source publishing | Every fixture inside the published universe lights the colour its channels say. The buffer path reads no grid, and the decode binds a black stand-in for each grid texture the layout lacks rather than skipping the dispatch, since an unbound compute texture is an error even on a branch not taken |
| G8 | — | The rig's movers and one of each fixture prefab whose body or beam reads the grid (flasher, laser, light bar, 15-channel bar, 6x4 strobe, and the four realtime prefabs' bodies), lit from the same channel values painted on a Horizontal, a Legacy and a Vertical grid at each layout's decode-texture size, the manager set to the matching layout, captured from in front of the prefabs | The Legacy and Vertical frames match the Horizontal one, both with the layout set before the manager came up and with the manager brought up on Horizontal and switched by `SetDmxInput`. Shader-driven bodies and beams read through both includes, the realtime bodies and the realtime light all follow the manager's layout, so one prefab serves every layout. The Vertical case guards the grid's texel size, which every reader takes from the manager's `_VRSLDMXTexelSize`. Channels that share a texel in any layout carry one value between them, so the frames compare the read and not the layout's capacity (G6) |
| G9 | — | Every `.shader`, `.hlsl`, `.cginc` and `.compute` under `Runtime/Shaders`, read as text | `_EnableCompatibilityMode` appears only in the two CRT shaders (each layout has its own CRT set, so for them the material is the layout), `_EnableVerticalMode` nowhere, the shared read core (`VRSLDMX.hlsl`, which the URP include goes through) and the Built-in include choose Legacy by `_VRSLU_DMXGridLayout == 2`, every shader including the Built-in read functions has the global declared (the URP include brings its own Defines file), and `VRSLDMX.cginc` is gone |
| G11 | — | The rig on the shipped Grid Textures with no channel source, each layout's video grid painted with a pattern of its own (`VRSLDMXRig.PaintVideoGrid`: every channel over the block of video texels behind the decode texel the fixtures read it from), switched Horizontal, Vertical, Legacy and back to Horizontal by `SetDmxInput`; after each switch strobe held while the colour settles, then 60 frames with strobe running, every fifth fixture's strobe channel high and the rest at zero | After each switch every fixture lights the new layout's pattern through that layout's own decode chain (5 frames to settle on Horizontal and Vertical, 10 on Legacy), the layout global and the live main texture follow, and the fixtures re-collected after the switch are the ones already held, so the light data keeps its order. On Horizontal and Vertical every strobing fixture changes state and every held one stays on. **On Legacy the row only judges that something strobes**: its strobe timing and output textures are 25 by 25 against a 50 by 50 grid, as upstream ships them, so which fixtures strobe depends on the neighbour a strobe texel samples. In batch mode nothing gives the decode textures a shader clock between frames, so the row sets `_Time` and `unity_DeltaTime` itself from a `LateUpdate`. The strobe output reads the main grid under `_VRSLU_DMXGridStrobeStatus`, a name Render Graph never sets back to black after a frame |
| G12 | — | A manager on Grid Textures holding only a Horizontal set asked for Vertical by `SetDmxInput`, for input 7, and given Legacy by writing the field; then a second, enabled manager that does not own the singleton asked for Legacy, and the owner switched off | Each refused switch logs one warning saying why, in the author's words, and changes nothing: the setting stays Horizontal (the field is put back) and the layout global stays 0. The non-owner takes the setting and touches neither the global nor any texture's update mode; handed the singleton, it binds Legacy and publishes it (C6 to C8's rule for a non-owner) |
| G13 | — | The control panel on the rig, its light slider assigned, the manager switched to Vertical with Vertical's video grid painted as in G11; the slider halved, then the panel's strobe toggle pressed | Held fixtures cast half the light after the switch. The Vertical strobe output closes somewhere while strobe runs, and with the toggle on it reads open everywhere for 30 frames: the toggle sets the manager's `disableStrobe`, which the manager publishes as `_VRSLU_StrobeDisabled` and the strobe output decode reads, so it reaches every layout without writing to a material. Every float, int and keyword on the fifteen decode materials is unchanged; their dirty counts are no use as the check, since the decode chain's own updates raise them. Run it in its class or the full suite: alone it reads a run-shape difference as the panel writing to decode materials |
| G14 | — | The standard 30-row player sweep, the package before grid layouts on one manager against after, on one machine, editor and host project; each player captured more than once, interleaved, so each row's run-to-run spread sits beside its A/B. And G11's log of a switch's cost | A manager that never switches costs what it did: the median change in the package's GPU cost is inside the median run-to-run spread, no row is slower by more than its own spread, and emitting fixtures match row for row. A stored reference from another editor can't judge this, since `Compare` refuses across editors, which is why the row is an A/B. A switch's bind and the fixture refresh after it cost fractions of a millisecond, and the incoming decode chain settles in 5 frames (Horizontal, Vertical) and 10 (Legacy) |
| G15 | — | **Hand row.** Play mode, the shipped Legacy manager prefab and one realtime fixture, no channel source, the Legacy video grid painted with a value per cell, then **DMX Monitor** | `Reading` shows `Video grid — Legacy`, naming any source on standby (`Video grid — Legacy (VRSL_SyntheticDMXChannelSource on standby)`), the Grid line names the 50 by 50 Legacy decode texture and one universe, the universe selector reads 1 of 1, and the only note under it is the nine-universe one: nothing says Legacy cannot be read. Every channel shows the painted values and the padding past 512 reads dark. Leaving Play mode puts every decode texture's update mode back |
| G16 | — | G8's nine prefabs and the rig's movers, captured once patched at sectors 2 to 10 and once at sectors 106 to 114 (channels up to 1524, the end of the Horizontal grid's third universe), the same values painted at both | The two frames match at I1's budget. Both includes size the grid from the manager's `_VRSLDMXTexelSize`, and a wrong size moves the read further from its cell the further down the grid a fixture sits. G17 covers the grid's last sectors and G20 what lies past them |
| G17 | — | `VRSLGridDecodeTests.G17`, once per layout: every channel the layout carries (Horizontal 1560, Vertical 871, Legacy 650) painted in its own cell with a value its neighbours don't share, the cells derived from channels no reader can place wrongly (`VRSLDMXRig.OwnCellsOf`: slot 1 of each sector, and the slots of a reference sector), then read back through the compute and through both includes' value, movement and raw (spin) reads. **Nine-universe half**: a Horizontal grid carrying nine universes, 1 to 3 in red, 4 to 6 in green and 7 to 9 in blue, every channel of all nine painted with a value of its own, read through both includes with the nine-universe flag on | Every reader reads every channel from its own cell, a sector's 13th included; channels sharing a Legacy texel are left out. A reader dividing by 13 in floating point misreads the multiples of 13 where float32 rounding lands above the integer. A universe is 520 flat channels, so a reader counting universes of 512 misreads Horizontal 1537 to 1560 (universe 3's last slots) and, in the nine-universe half, the last slots of universes 3, 6 and 9, each of which should read its own cell's own component |
| G18 | D | `VRSLGridDecodeTests.G18`: a test shader reads a mover's pan and tilt through the URP include from a painted Horizontal grid, coarse pan 100 and fine pan 200, coarse tilt 60 and fine tilt 30, at a 180-degree sweep each way, with fine channels off and then on | Off, pan and tilt are the coarse value across the sweep; on, each takes its fine channel as a 256th of a coarse step, as the compute does for the realtime light, to within 0.01 degrees. A mover body that ignores fine channels moves in coarse steps while its beam moves smoothly |
| G19 | — | `VRSLGridDecodeTests.G19`, once per layout: G17's painted grid read through the compute and both includes' value, movement and raw reads with the grid texture set to Point, then to Bilinear | Every reader returns the same value for every channel both ways: each samples the grid point by point and clamped, whatever the texture carries. Render textures default to Bilinear, so a Grid Textures asset an author fills with their own would otherwise blend each channel with its neighbours in any reader taking the texture's own sampler |
| G20 | — | `VRSLGridDecodeTests.G20`, once per layout: a grid grey in every texel with its strobe output closed everywhere, then channel 0 and channels past the layout's last cell (the first, second and seventh past it, the next sector's first two, and two further on) read through the compute's value, movement, spin and strobe reads, both includes' and the public include's (F1's probe), beside a few channels the grid does carry | Every channel the grid doesn't carry reads as a channel at 0: value, movement and spin 0, strobe open. A 13-channel fixture patched wholly past the grid reads black through `VRSLDMX_Color`. The carried channels read grey with strobe closed, so the row isn't passing on a grid nothing reads. A reader that clamps to the grid's edge, or wraps into another universe's cell, reads grey. Channel 0 is the address of a fixture left unpatched; read as a cell it lands on slot 13 of sector 0, channel 13's |

### DMX input

`VRSLDmxInputTests` in the suite. The DMX manager's **DMX Input** chooses where the lights
take their DMX from: the Horizontal, Vertical or Legacy grid, or Stream data, the channel
source (Truss records from the stream, OSC, the synthetic source). It is the event's choice,
so nothing switches it but `SetDmxInput` or the inspector: no input is detected from what
the stream carries, and nothing falls back when a source stops. Under a grid input a source
stays bound, on standby: it keeps being drained so Stream data reads current values the
moment it is chosen, but nothing is published. Under Stream data no grid decode texture
runs, and with nothing sending it the lights are dark and the manager says why once, five
seconds of play later.

| # | Path | Scenario | Expected |
|---|---|---|---|
| E1 | — | The rig on the shipped Grid Textures with the synthetic source attached (Fixtures, held still), each layout's video grid painted with a pattern of its own as in G11, strobe held; cycled Stream data, Horizontal, Vertical, Legacy, Stream data, Horizontal by `SetDmxInput` | Every fixture lights the chosen input's values: Stream data the frame after the call, each grid within its chain's settle time (5 frames Horizontal and Vertical, 10 Legacy). Under a grid the source stays bound and the channel count is 0; the layout global follows. The source's values match none of the grid patterns, so a fixture reading the wrong input shows |
| E2 | — | **E1's image half.** N29's and N30's scenes with the synthetic source and a grid of other values both present, brought up on Stream data and switched to the Horizontal grid at runtime, and the other way round | Each frame matches the same scene brought up on the input it was switched to, shader-driven bodies and realtime lights alike. The two inputs' frames differ, which is the row's sensitivity |
| E3 | — | `SetDmxInput` given 7, -1 and 4 on Stream data, and 9 on the Vertical grid; then Stream data chosen with no source | Each number that is no input logs one warning naming the numbering and the input the lights stay on, and changes nothing: the source is still read. Stream data with no source is not refused. G12 covers a grid the Grid Textures can't decode |
| E4 | — | The rig on Stream data, lit from the synthetic source, then the source component switched off | The lights are dark two frames later (every fixture's light data zero, no fallback to a grid or to the buffer's last values) and stay dark. No message in the first four seconds; one at the five-second grace, saying Stream data has nothing sending it and what to add; none after. Switching the source back on lights the rig again |
| E5 | — | The rig with no source: Vertical applied twice; Stream data chosen a second before a synthetic source enables; a second manager, enabled after the owner, given Legacy twice; then the owner switched off | Twice ends as once (same live texture, update mode and layout global). Stream data chosen early reads the late source with no message. The spare takes the setting and the owner is unaffected. Handed the singleton, the spare keeps its own input (Legacy) and the source moves with the singleton, on standby: count 0, Legacy's textures live |
| E6 | — | `VRSLGridLayoutTests.E6_a_manager_not_yet_looked_at_comes_up_on_its_grid_or_its_source` and its siblings: a switched-off manager the upgrade hasn't looked at (saved with 0.2.0-rc.6, or added since: its input and no version), once per grid, with and without an enabled channel source in its scene, through the enable path, the deferred validate path and the scene build step; then a manager already looked at and one whose input a call chose, each beside a source, and an rc.6 Vertical manager beside one | Without a source each comes up on its own grid; beside one, on Stream data, since 0.2.0-rc.6 read a source whenever it was sending. Every case is marked as looked at and is never changed again: the looked-at and the call-chosen managers keep their grids beside a source. The rc.6 rule runs first (the old texture is cleared) and the source rule after it. The shipped manager prefabs carry no version, because a scene's manager inherits what its prefab says and would otherwise never be looked at |
| E7 | — | The rig's manager on Stream data reading the synthetic source, then on the Vertical grid, then on Stream data with no source, `VRSL Diagnostics` logged in each | The summary names the input on a line of its own: `DMX input: Stream data from VRSL_SyntheticDMXChannelSource`, `the Vertical grid, VRSL_SyntheticDMXChannelSource on standby`, and `Stream data, and NOTHING IS SENDING IT` with what to do. A Truss source that has decoded no record yet is named as such. The **VRSL Diagnostics** context-menu entry is on `LogDiagnostics`, where an author reaches the report |
| E8 | — | **The choice.** `VRSLBasisVideoOutputTests` (Basis integration only). The Basis video output's target and corner choice as a pure function, against the shipped Grid Textures | Following on, the live layout's raw grid (`DMXRTViewer-RAWValues-<layout>`, found as the texture its main decode texture's material samples) with that layout's corners; following off, the DMX Grid RT with the single corners; under Stream data nothing, either way; with no manager the DMX Grid RT; an author's main texture that is no decode texture gives no raw grid and falls back to the DMX Grid RT with the layout's corners |
| E9 | — | **A real player.** `vrsl-dmx-marching.ts` playing, the output following the manager with the fixture's strip framed in all three corner sets and the Horizontal raw grid as its DMX Grid RT; the manager switched Horizontal, Vertical, Legacy, then Stream data with every raw grid blackened, then Horizontal | On each grid only that layout's raw grid receives the frame and the other two stay black (the outgoing one is cleared). Under Stream data none is written over five frames. The first frame after choosing Horizontal again writes its raw grid |
| E10 | — | The standard player sweep on Stream data and on a grid input, against the package before a grid input put a source on standby; both players built from clean checkouts, three captures each in interleaved rounds | Stream data costs less by the decode chain it no longer runs, and a grid input costs what it did. Dark-grid captures are bimodal from run to run, so on an unpainted grid the grid half rules out only a large change. Build each player from a clean checkout: a working tree whose decode textures were saved as Realtime keeps the chain running in the player (N35) |
| E11 | — | **Hand row.** Play mode in a Basis project: the shipped Horizontal manager, eight movers at sectors 0-7, the synthetic source (Fixtures) bound, and a recorded show whose Horizontal grid sits in the bottom of a 1080p video, framed into the Horizontal raw grid every frame by the video output's `BlitUVs`. A uGUI Dropdown with the four options, `On Value Changed (Int32)` wired to `SetDmxInput` by a persistent listener, set to each in turn | Every choice switches every fixture: Horizontal lights all eight from the show; Vertical and Legacy are dark, nothing writing those grids; Stream data reads the source; back on Horizontal the show again. The DMX Monitor's header follows (`Video grid — Horizontal (… on standby)`, `Stream data — …`, and with the source unbound `Stream data — nothing is sending it, lights dark`), and the inspector shows the standby line on a grid and "Reads Synthetic DMX Source." on Stream data. The synthetic source binds only in its own `OnEnable`, so in a scene entering Play it can miss the manager and needs assigning |
| E12 | — | **Basis.** Whether a Basis world bundle may call `SetDmxInput` from a UI event, and from an interpreted world script | Only once Basis approves the manager. World scenes load with content removal and persistent-listener scrubbing on, and Basis's scene content list and Cilbox allow-list carry no VRSL type, so on stock Basis the manager is removed from a loaded world and a listener aimed at it is switched off. Check against the Basis a world actually ships on |
| E13 | — | **E5's two-client half, hand row.** Two Basis clients in one world with the synced input recipe from the wiki; one changes the input, then a third joins | Both clients follow the change, and the late joiner comes up on the current input with no message about Stream data. Needs a world that can reach the manager (E12) |
| E14 | — | **Hand row, the real Basis path.** A Basis project booted from its initialisation scene in Play mode; a rig added at runtime: the shipped Horizontal manager, eight movers at sectors 0-7, a `BasisMediaPlayer` with the SEI record source (`BasisUserDataToVRSLDMX`, three universes) and the video output following the layout with the marching strip's corners, and the Dropdown wired to `SetDmxInput`. `LoadUrl` on `vrsl-dmx-marching.ts`, the URL prompt accepted by hand; then the dropdown switched Horizontal grid, Stream data, three times each way while it played. Every fixture's ramp offset recovered from its decoded red channel (the fixture puts `(c - 1 + 5 * frame) % 251` on channel c) | Under the grid input the source is on standby (0 channels published) and keeps decoding (records counting on across every switch, none dropped); under Stream data it publishes 1560 channels. All eight fixtures agree on one offset each time, and it names the frame: the grid within a frame of the one presented, the records exactly from the first read after each switch. At the end of the video both inputs read the last frame |

### Reading DMX in your own shaders

`VRSLDmxReadTests` in the suite. A world's own shader reads DMX through
`Runtime/Shaders/VRSLDMX.hlsl`: `VRSLDMX_Channel`, `VRSLDMX_ChannelRaw`, `VRSLDMX_Strobe` and
`VRSLDMX_Color`, each taking an absolute channel. The rows read it through a probe shader
(`Hidden/VRSL-URP/Tests/DmxRead`) that includes only URP's Core and that file and declares its
own `UnityPerMaterial`. It is drawn as an ordinary scene renderer by a camera of its own, set to
`Skip` so VRSL lights nothing in it, into a float target a texel per channel: over the whole
target from the fragment stage, or as a point per channel from the vertex stage. It reads the
globals the manager publishes, not textures set on its material, which is how a world's shader
sees them. Each texel holds the reads offset into 0.25..0.75, so one nothing wrote can't pass for
a read of 0.

| # | Path | Scenario | Expected |
|---|---|---|---|
| F1 | D | Once per grid layout: the main, raw and strobe output textures painted with different values (the raw one at another shape than the decode textures), the cells from `VRSLDMXRig.OwnCellsOf`, the manager on that grid; and once under Stream data from the synthetic source's Ramp, strobe running. Every channel read through the four functions from both stages | The channel read gives the main grid's value and the raw read the raw grid's, the raw grid's cells found from its own size; under Stream data both give the byte the source sent. The strobe read gives what the compute's strobe gives for that channel, under Stream data on a frame where some strobe channels are shut and some open. The colour read gives channels +7, +8 and +9 times the dimmer at +5. Vertex and fragment agree on every channel. Channels sharing a Legacy texel are left out |
| F2 | D | Once per grid layout on the shipped Grid Textures, the real decode chain with its clock running (G11's): a channel held at one value until both reads settle, then stepped on the layout's video grid; and under Stream data the synthetic source's Sweep, read for 30 frames | On the first frame after the step the raw read has the new value and the smoothed read hasn't reached it; within the next few frames the smoothed read sits strictly between the two, and later it meets the raw one. The shipped raw slot is the video grid the layout's decode chain reads. Under Stream data, which has no colour smoothing, the two reads are the same every frame while the values change |
| F3 | D | The probe drawn once; `ShaderUtil`'s SRP Batcher check on its active subshader. And a test shader with the include beside URP's `LitInput.hlsl` and beside `DeclareDepthTexture.hlsl`, each in both orders, compiled vertex and fragment for Windows and Android Vulkan | The SRP Batcher accepts the probe: including the file adds nothing to a shader's material properties. Every pairing compiles. The package's internal includes fail here, redefining `_MetallicGlossMap` or `_Metallic` beside the Lit inputs and the depth texture beside URP's. The check reads "not initialized" until a shader has drawn; still reading it after a draw, the row is inconclusive |
| F4 | D | The manager on a painted Horizontal grid with no package fixture in the scene, once with none ever there and once after the rig's realtime fixtures rendered and were all switched off | The probe reads the painted grid both times, not black. Z7's sibling for a shader of a world's own: Render Graph sets the grid back to black after each camera that published it, so a manager that stopped publishing at zero fixtures would leave the probe dark |
| F5 | — | The probe compiled, vertex and fragment, for Windows (D3D, serving Direct3D 11 and 12), Android Vulkan plain and with `STEREO_MULTIVIEW_ON`, and desktop Linux Vulkan | All eight programs compile. T1 and T2 cover the package's own shaders, not one that uses the include from outside |

### Migration

`VRSLMigrationTableTests`, `VRSLMaterialReferenceTests` and `VRSLScriptReferenceTests` in the
suite, and two hand rows. The migration tool (**VRSL → URP → Migrate Scene Fixtures**) compiles only in a
project with `com.acchosen.vr-stage-lighting` installed, which none of the test projects
has, so its table, its sibling placement and its field translation live in the runtime
assembly, where the suite reads the table and drives the rest with stand-ins for the
original package's components. A fixture
prefab works on any grid layout, so Legacy and Vertical upstream fixtures migrate onto the
Horizontal URP prefabs. That they then light correctly on a Legacy or Vertical grid is G8,
which names the flasher, laser, light bar, 15-channel bar and 6x4 strobe.

| # | Path | Scenario | Expected |
|---|---|---|---|
| L1 | — | The migration table against this package | Every upstream GUID appears once across the pairs, the unported list and the light stencils, and every URP target is a prefab inside this package |
| L2 | — | Every material under `Runtime/Materials/Lights`, its shader and texture references read from the asset file | Each resolves inside this package or a package it depends on, and loads as a shader or a texture as its slot expects. AudioLink isn't a dependency: only the AudioLink set (a folder named `AudioLink` or `AudioLink-Scenes`) may point into it, as the AudioLink movers' `_AudioSpectrum` does at `rt_AudioLink`, and where AudioLink is installed those references must resolve there; without AudioLink the set is left out. Slots the shader doesn't declare are skipped: they're never sampled and many carry upstream references. Read from the file because a `Material` can't tell a missing reference from an empty one |
| L4 | — | A throwaway project with AcChosen's VRSL v2.3.0 (it carries both Legacy sets, and needs stand-ins for UdonSharp and the VRChat SDK to compile) and this package, a scene holding one instance of every Legacy fixture prefab from both sets and a VRSL-URP manager set to Legacy, every serialized value on each legacy fixture component moved off its default; run the migration twice and compare each sibling with its source, then light a known pattern | The summary pairs every mapped fixture, counts them by name, and names only the 5-channel Vertical light bar as left behind; each URP sibling sits right after its source, in its pose, and is active, on the layer and tag, and has its fixture component on or off, as its source; it carries every value the tool copies, the AudioLink band included (an int in v2.3.0, an enum here), with tints, intensities and band multipliers scaled by the two prefabs' defaults and each AudioLink mover's cone width as its spot angle (L10's conversion); it answers its own channels. The second run adds nothing |
| L5 | — | L4 against AcChosen's current `main` (the first Legacy set only; its three `GetInstanceID()` calls need patching to compile from Unity 6000.5) | As L4 |
| L6 | — | Every prefab and scene in the package, read from the file | Each script a component names loads as a component (a MonoBehaviour) from this package or a package it depends on (dependencies of dependencies count: URP's `Volume` is in the core package). Only the AudioLink set (as L2) may carry AudioLink's own scripts, as the AudioLink example scene does, and those must load where AudioLink is installed; without AudioLink the set is left out, since its own components don't compile there either. A VRChat script, or one whose whole body compiles only under UdonSharp, would arrive in every scene as a missing script |
| L8 | — | `VRSLScriptReferenceTests.L8`: every public type in the package's assemblies (`Towneh.VRSL.URP` and `Towneh.VRSL.URP.*`, the optional ones where they compile) | Each sits in a namespace. A type in the global namespace clashes with one of the same name in another package or the project. A failure names each type and its assembly |
| L9 | — | `VRSLMigrationTableTests.L9`: the table's light stencil entries and the summary paragraph built from them | The table holds the original VRSL's eight light stencil assets by GUID (the four mask prefabs, the light and restoration stencil materials, the two shaders), each under its own name. The paragraph names each one found with its count, says how to keep light off an area without a stencil (aim, cone, range, and that a wall doesn't stop the light), and is empty with none found. The tool's scene walk needs the legacy package, so no suite row covers it |
| L10 | A | `VRSLAudioLinkConeWidthTests.L10`: `SpotAngleForLegacyConeWidth` on each shipped AudioLink prefab's realtime light, across the original cone width's range (0 to 5.5) | On the spot and wash movers the original prefab's width (2.25, 2.7) gives this prefab's angle, a width of 4 scales it by the same share, and the angle never narrows as the width grows and stays between 5° and 179° (0.15 gives 5°). On the par, blinder and discoball every width gives the light's current angle, since the original's width never reached their shaders |
| L11 | D | `VRSLMigrationCopyTests.L11`: a stand-in for the original's DMX wash component on a switched-off object on a layer and tag of its own, the component off, placed onto the DMX wash prefab | The sibling is switched off, on the source's tag, every object of it on the source's layer, its realtime light off |
| L12 | A | `VRSLMigrationCopyAudioLinkTests.L12`: a stand-in for the original's AudioLink wash component at the original prefab's values (global intensity 1, band multiplier 2.5), then one at half the intensity and twice the band multiplier with AudioLink off and band 2, each placed onto the AudioLink wash prefab | The untouched one arrives at this prefab's global intensity, band multiplier and final intensity, AudioLink on and the open gobo; the changed one at half this prefab's intensity and twice its band multiplier, AudioLink off, band 2 |
| L13 | D | `VRSLMigrationCopyTests.L13`: the L11 stand-in at the original prefab's values (pan and tilt inverted, tilt range −180), placed onto the DMX wash prefab | The sibling inverts pan and tilt with a tilt range of −180 though this prefab inverts tilt only, and arrives at this prefab's intensities and tint colour |
| L14 | D | `VRSLMigrationCopyTests.L14`: the L11 stand-in with its cone length changed from the original prefab's 8.5 to 55 and its inspector fold open, placed onto the DMX wash prefab; then one at the original prefab's values | The changed one is named in one warning with both values and counted under cone length only; the untouched one names nothing |
| L15 | D | `VRSLMigrationCopyTests.L15`: a stand-in for the original's DMX component and this package's DMX Static on one object, a holder with a field that takes any component, a field typed to the stand-in, button calls on the stand-in to `ApplyAddress` with no argument (ours has it), to a method ours lacks, to `set_enabled` carrying a bool, and to `ApplyAddress` carrying an int (ours takes none), and calls from a bool event to `set_enabled` and from an int event to `ApplyAddress`; references moved from the stand-in to ours | The any-component field, the no-argument `ApplyAddress` call and both `set_enabled` calls point at ours; the typed field, the call to the missing method and both calls passing an int are untouched and handed back; four moved |
| L16 | D | `VRSLMigrationCopyTests.L16`: an original carrying the L11 stand-in and a child named like one of the DMX wash prefab's, an instance of that prefab as its sibling, and a holder outside pointing at the original's root, child and fixture component, with another inside the original pointing at its root; then two instances of the prefab with an object added under one | The three outside references are listed, each with its counterpart (the sibling, its same-named child, its realtime light); the inside one isn't. The added object is listed once, with its parent's counterpart on the sibling |
| L17 | D, A | `VRSLMigrationCopyTests.L17` and `VRSLMigrationCopyAudioLinkTests.L17`: a stand-in for the original's component, turned about every axis and moved, scaled 1.5 and then (1, 2, 0.5), placed with the migration table's pair onto the DMX 13CH and 5CH par and blinder and the AudioLink par and blinder | Each body sits where the original's did: at the original's root, unrotated, at the original body's scale (1.3221 on the blinders), whatever the original's pose and scale. Each light shines down the original's −Y, as the original's beam did |
| L18 | A | `VRSLMigrationCopyAudioLinkTests.L18`: an AudioLink blinder placed for the L17 stand-in, then moved onto the original's root, then 1 cm from where it was placed | A re-run counts the first two as the original's sibling and skips it; the third it doesn't |

### Measurement harness

These judge the instrument rather than the package, and they exist because a measurement
tool that is trusted and wrong is worse than no tool. **Re-run them after any change to how
results are read** — that is the change most likely to break them silently.

`VRSLBenchmarkTests` in the suite, up to H7. They measure, so they are slower than the rest
of it and they render at 1024 square: at the rig's default 256 there is not enough
per-pixel work for a real regression to clear the noise, and a row that cannot fail under
the fault it is looking for is worse than no row.

**H10 to H17 are hand rows**, run with the sweep player and the command-line comparison
(**The headless gate**) rather than the suite. They are about a built player, and the suite
runs in the editor, which is the difference they exist to measure, so there is nowhere for
them to live inside it.

| # | Path | Scenario | Expected |
|---|---|---|---|
| H1 | — | **The null run.** Capture, change nothing, capture again, compare | Every row **unchanged**, and the stated noise floor at or above the spread the two runs actually showed. Judge the guards as much as the verdict: each capture must report the package costing something. A run of zeroes compares as unchanged against anything, so a row of all-zeroes passing is the failure this row exists to catch, not a pass |
| H2 | — | **The verdict.** Two synthetic runs alike but for one row's package cost | A move past the floor reads as regressed, an equal move the other way as improved, and anything inside the floor as unchanged, including a delta exactly equal to it. Judged on the comparison rather than on a seeded build, so it runs on any clock. H14 takes a real workload change end to end |
| H3 | — | Three captures of an unchanged scene; measure the spread across all three | The spread must be within a tolerance **declared in code**: 40% of the measured cost, with a 0.05 ms floor for costs too small for a fraction to mean anything. Judged against a declared figure rather than one derived from the same three runs, because a floor taken from the data it adjudicates cannot fail. It is a smoke test for a harness that has stopped working, not a precision certificate: batch mode reproduces poorly and inconsistently, and a loaded machine fails it |
| H4 | — | Any capture, first one in the process | Must be **discarded**. A session's opening capture runs pinned at exactly the capture delta, so the difference cancels to zero while the counters look perfectly healthy. Idle frames do not clear it and neither does a longer warm-up inside the capture; disposing and rebuilding the rig does, which is why the warm-up is a whole capture taken and thrown away |
| H5 | — | Quality `Standard` against quality `Off` | Two halves. The **counter** half must pass anywhere: steps per light reads 24 at `Standard` and **0** at `Off`, which says the volumetric pass is not being enqueued rather than merely being configured down. Clearing `volumetricShader` alone does not do it (the manager builds its material once and keeps it). The **timing** half needs a GPU clock, direction included: on the CPU clock the run-to-run spread is wider than the gap, so the row reports inconclusive headless |
| H6 | — | `VRSLBenchmarkScene.SetActiveFixtures` at each count in the matrix | The manager collects exactly the count the row claims at 10, 25, 50, 100 and 200. The sweep varies fixture count by activating an evenly spaced subset of one truss, so the spread is identical at every count; rounding collisions at small counts are how this goes wrong, and it goes wrong silently |
| H7 | — | `VRSLBaseline.ReferencePath` with `VRSL_PERF_HOME` unset, set to a folder holding no `baseline.json`, and set to one that does | Null, null, then the path. The middle case is the one worth having: a consuming project may set the variable for the reference frames alone. The rows restore the variable in teardown, not at the end of the row — it is process-wide and the image rows read it too |
| H15 | — | `VRSLBaseline.ReferenceFor` with a machine that has its own file under `baselines/`, and with one that does not | The machine's own `<gpu>-<context>.json` where there is one, `baseline.json` where there is not. **Keyed by GPU and context both**, for the same reason the noise floor is: an editor run and a player run measure two different things and neither describes the other |
| H15b | — | `VRSLBaseline.ReferenceFileName` with a context naming a traversal — `../outside`, `..\outside`, `/etc/passwd`, `..`, `a/../../outside` | One path segment, no separator and no `..`, and the lookup never reaches a file outside `baselines/`. **The context is input, not a constant**: it is read from the candidate `run.json` the caller was handed. Both halves of the name are reduced to letters and digits rather than allow-listed, so a new context works without reading another's numbers |
| H16 | — | A run compared against a reference captured before `captureWidth` / `captureHeight` existed | **Compared, not refused.** Zero means the size was never recorded, which is not the same as a size that disagrees |
| H17 | — | A **player** run compared with no `-baseline`, from inside the editor | The reference is chosen from the **candidate's** environment, not from the process doing the comparing. Otherwise a player run judged from the editor resolves the editor lineage, falls back, and is refused for a difference nobody introduced |
| H8 | — | `VRSLQualityPreset.Session` against an AudioLink-only scene, a scene with both managers, and no manager at all | Levels apply to whichever managers are present, and Restore puts the author's own values back. **The both-managers case is the one with teeth**: quality held on the DMX manager alone leaves the AudioLink volumetrics running at every level, so the `Off` capture is not off. Strobe is held on only where the manager has that switch — the AudioLink path strobes off the audio and has none. The rows build their managers on an **inactive** GameObject, so `Awake` and `OnEnable` never fire. The AudioLink and both-managers cases are `VRSLAudioLinkQualityPresetTests` in the AudioLink test assembly; the no-manager and strobe cases are in `VRSLQualityPresetTests` |
| H8b | — | `VRSLBenchmark.MeasureDmxPath` across one path, both paths, and both with the DMX one empty | The measured path is never a manager holding no fixtures while another holds some. A single empty manager still wins, because there is no other path to name. The case worth the row is both present with DMX empty: selecting it would write a measured-path count of **zero**, which is what a run too old to carry the field deserialises to, so the scene would read as single-path with nothing saying the figures came off a path with nothing on it |
| H9 | — | `VRSLCounters.MeasuredFixtures` and `MixedPaths` on a single-path scene, a dual-path one, and a run recorded before the field existed | The coverage figure divides by the path it was measured on, not by the scene. They agree wherever a scene has one light path. **In a dual-path scene they must not**: the average comes off one cull pass, so read against the scene total it reports less coverage than was measured. The summary says which path it is describing. An older run deserialises the field to 0 and falls back to the scene count |
| H10 | — | **The player run.** Build the sweep player and run it, which captures the matrix in a standalone player | 30 rows, **every one of them on a GPU clock**, with the environment block reading `Context: Player` and naming the scripting backend. Fixture counts exact at 10, 25, 50, 100 and 200, and **Emitting** non-zero on all 30. Emitting can read a few short of the count under Ramp, the same handful every run: those fixtures' colour channels sit under the compute's `6/255` emission gate |
| H11 | — | A player run compared against the reference run, which is an editor one | **`REFUSED`, a zero-row verdict line and exit 2.** `Matches` reports the first difference it finds, so the named difference may be priming rather than context. This is the guard that keeps an editor figure and a player figure out of one table |
| H12 | — | `git status` on the host project before and after building the sweep player, **including a build killed part-way through** | No new differences, and no `Assets/VRSL-BenchBuild`. The build step writes no Player Settings, and discards its scratch scene on the way in as well as out. **One thing it cannot help**: `GraphicsSettings.asset` gains an always-included shader entry, which Unity does on any batch standalone build of that project, and which has to be put back by hand |
| H13 | — | `VRSLBenchCommandLine.AdoptFloorFromCommandLine` with `-baseline` and `-candidate` naming a **player** null pair, then check the editor's stored floor | Filed under `VRSL.Perf.Floor.Player.<gpu>`, leaving the editor's bare-GPU key untouched, since player and editor noise differ. Refuses a forced comparison and refuses runs captured on another GPU: a floor is raised and never lowered, so a wrong one survives until somebody resets it |
| H14 | — | **The stopwatch.** A full before-and-after: capture, change the package, capture, compare | Under fifteen minutes of wall clock and under two minutes of attention. A deliberate workload change (doubling the Standard step count, say) regresses the rows it affects and not the others. **Time the steady state, not the first run** — the first build of a player on a machine is a cold IL2CPP build of the whole host project, and the first launch pays for shader and driver caches that every later one reuses |

### Image regression

Rendered frames compared rather than eyeballed. `VRSLImageRegressionTests` in the suite.

The default comparison is against **this machine's own previous capture**, not a stored
image: two GPUs don't render identically, so a shared reference fails everywhere except
where it was made. A reference frame from elsewhere is the second mode, read from a folder
named by `VRSL_PERF_HOME`; rows needing one skip with a message when it is unset.

Captures freeze everything that integrates over time. Strobe alternates, gobo spin never
settles, and movement damping converges only after a warm-up. Without that freeze a row
compares two frames of the same scene at different moments and reports a difference that
is entirely the clock.

| # | Path | Scenario | Expected |
|---|---|---|---|
| P1 | — | Capture with `lightCullShader` assigned and again with it cleared | **Not one pixel different.** The cull decides which lights a tile iterates, never what they contribute, so any visual change means it is dropping a light that reaches the tile. This is the image half of P1 in **Cost and culling**, not a second row that shares the name |
| I3 | — | **Sensitivity.** Capture a frame, shift it by one pixel, compare | Detected. Seeded by shifting a real captured frame rather than by a debug keyword in a shipped shader — the claim is about what the comparator resolves |
| I4 | — | **Specificity.** Capture the same unchanged scene twice | **Identical.** Without this row the sensitivity row above is satisfied by a comparator that calls everything different. Identical also says the capture freeze works |
| I1 | — | Compare against this machine's last capture. **Seed it from the same shape of run you will verify with — a full suite run, normally** | At most **0.25%** of the frame differing, the budget for the edge scatter depth priming leaves behind. A size mismatch is refused separately, since it means nothing was compared rather than that nothing moved. Past the budget the row says what moved and writes the images. The first run on a machine seeds the stored frame and reports inconclusive; delete the stored image to re-seed after an intended change |
| I2 | — | Compare against a reference frame in `VRSL_PERF_HOME` | Identical **on the machine that took the reference**. Expect a difference anywhere else and treat it as hardware, not regression: a frame taken on another GPU fails here while I1 passes in the same run. Skips cleanly when the variable is unset, which is how a routine run should be made |
| I5 | — | A manager with every wiring field cleared, bounced so the clear takes effect, repaired, and bounced again | **The same image as one wired by the prefab.** This is the only row that can make the claim: comparing GUIDs proves resolution picked the assets the table names, never that the table names the right ones. **Bounced twice**: the cull resolves its shader in its constructor and the manager builds its volumetric material once and keeps it, so without the first bounce the row compares two correctly wired managers. Any row that changes a shader field has to drop the cached materials for the same reason |

**Seed and verify in the same shape of run.** The host can change rendering state partway
through a run (it brings the GPU Resident Drawer up with its quality tier), so the rig
holds the drawer off, and depth priming and MSAA, for every render. A filtered run and a
full one then capture the same frame.

**What a frozen capture holds.** `FreezeForImageCapture` aims the beams at the floor at
their authored cone at an intensity of 60, in a frame nothing else lights, and the capture
helpers drive the source with the `Fixtures` pattern held still, every dimmer at full, so
what an image row compares is the package's light. Stored captures and reference frames
are filed under a name carrying the rig's `CaptureVersion` (`rig-default-v2`), so a
change of shape seeds afresh instead of comparing against an old frame. Bump the constant
with any change to what a frozen capture contains.

A failing row writes `-expected`, `-actual` and an amplified `-diff` PNG under
`VRSL-Benchmarks/image-failures/`, because a number cannot distinguish a global
brightness shift from one wrong beam. The difference is amplified 16× — a real
regression is often a handful of 8-bit steps, and an unamplified difference image is a
black rectangle whatever went wrong.

### The headless gate

The sweep player and the comparison ship in the package. The gate itself is the suite's
harness and image rows: the suite command above with
`-testFilter "VRSL.URP.Tests.VRSLBenchmarkTests;VRSL.URP.Tests.VRSLImageRegressionTests"`.

```
# Build the sweep player (or VRSL → URP → Performance → Build Sweep Player)
Unity.exe -batchmode -projectPath <project> \
  -executeMethod VRSL.URP.EditorScripts.VRSLBenchBuild.BuildFromCommandLine -out <dir>

# Run it: captures the standard matrix and quits
<dir>/VRSLSweep.exe -vrsl-out <runs> [-vrsl-dmx-input 0-3] [-vrsl-mirrors] [-vrsl-force-own-normals]

# Judge a run; with no -baseline, against the reference run
Unity.exe -batchmode -projectPath <project> \
  -executeMethod VRSL.URP.EditorScripts.VRSLBenchCommandLine.CompareFromCommandLine \
  -candidate <run.json> [-baseline <run.json>] [-out report.md] [-force]

# Adopt a noise floor from a null pair
Unity.exe -batchmode -projectPath <project> \
  -executeMethod VRSL.URP.EditorScripts.VRSLBenchCommandLine.AdoptFloorFromCommandLine \
  -baseline <run.json> -candidate <run.json>
```

The player writes each run to a folder of its own under `-vrsl-out` (the build folder
without it), holding `run.json` and `report.md`, and names the folder in its log on a line
starting `[VRSL sweep] report: `. `-vrsl-dmx-input` picks what the manager reads (Stream data
from the scene's synthetic source when it is left out), `-vrsl-mirrors` measures the mirror
matrix instead of the standard one, and `-vrsl-force-own-normals` draws VRSL's own normals
everywhere, for the half of a pair that measures what reading URP's is worth.

**With no baseline named, the reference run is used**, resolved from
`$VRSL_PERF_HOME/baseline.json` the same way the reference frames are, so a gate needs no
path anybody has to remember. That default is safe on a machine that did not produce the
reference: `Compare` refuses on an environment mismatch, so the answer there is a refusal
naming the difference and exit 2, not a fabricated delta. With the variable unset, or set
to a folder holding no `baseline.json`, the comparison fails and names the folder it looked
in rather than comparing against nothing.

**The editor's batch mode never captures a sweep.** It has no GPU clock, so a capture taken
there produces CPU-basis numbers nobody should quote in a results table. The sweep player
gets one by running the matrix in a standalone player. The capture loop is shared with the
editor window, and the verdict comes from the same `VRSLBaseline.Compare` the window calls,
so none of the three can disagree.

The build is the slow half and is incremental afterwards, so a before-and-after costs one
full build and one quick one; a second capture of an unchanged package reuses the player.
Only the build needs the editor closed. The player holds no project lock, so a capture can
run beside an open editor.

**A player run and an editor run are never compared.** `Matches` refuses on `context`, so
the two form separate lineages with separate noise floors, and `baseline.json`, an editor
run, is refused by a player candidate rather than reported against it.

The comparison exits 0 when nothing regressed, 1 when something did or the inputs are
unusable, and 2 when it refuses because the two runs are from different machines.
Refusing rather than failing matters there, since exiting 1 would train whoever reads the
gate to pass `-force` by reflex.

**A refusal writes a verdict line too**, reading zero rows, with the `REFUSED` line above
it saying why, so a wrapper that checks for the verdict line before `REFUSED` doesn't read
a refusal as a regression. Check it by hand whenever that path changes, because no
automated row covers it (the entry point ends in `EditorApplication.Exit`). Compare the
reference run against a copy of itself with `environment.graphicsDevice` altered: expect
`REFUSED`, a zero-row verdict line, and **exit 2**.

**A sweep measures nothing unless four things are true.** It must render at a fixed size
large enough to have work in it; at a Game view's size the GPU frame barely moves with the
lights per tile. Its DMX source must be assigned to the manager explicitly, because
the source's own `OnEnable` registration only lands if the manager already claimed the
singleton. Strobe must be held off, or a random subset of the rig is lit each frame and
the workload changes between rows. And it must own its scene, or a second manager
fights it for the singleton. The **Emitting** column in a report is the quickest check
that all four held: fixtures collected with none emitting means the run measured a dark
scene, whatever else its numbers say.

**A manager bounce rebuilds what `OnEnable` builds.** Configuring a manager and then
cycling it off and on silently undoes anything that works by dropping something the
manager rebuilds, quality `Off` most of all. Configure *after* the bounce, and let a change that needs
one, like clearing the cull shader, do its own.

**Batch mode has no GPU clock.** `FrameTimingManager` returns a CPU frame time and a GPU
frame time of exactly zero there. The harness falls back to the CPU difference, says so in
the run's notes, and marks every affected row's cost basis `CPU` so a CPU figure is never
quoted as a GPU one. Anything whose claim is about GPU cost therefore cannot be closed
headlessly — run it from the editor window or a player.

### Depth and priming

The setting that decides these is **Depth Priming Mode** on the active Universal
Renderer. With it on, URP renders a depth prepass and then draws opaque geometry with
an `Equal` depth test, so a shader whose depth passes do not reproduce its forward pass
is culled from the frame entirely — it draws nothing rather than drawing wrong, and the
symptom reads as a culling or LOD fault.

Basis ships priming `Forced` on its desktop renderer and `Disabled` elsewhere, so both
are live in one project and both need checking.

Run **`VRSL → URP → Validate Renderer Setup`** first, as an opening diagnostic rather
than as a substitute for the rows. It reports the priming and rendering modes, whether
the prepass layer mask covers the layers the scene's fixtures are on, and any shader
drawing opaque without both depth passes, whether this package ships it or a fixture in
the scene carries a custom one.

**What it cannot tell you is whether a depth pass that exists is the right one.** It
reads pass tags, so it settles presence and nothing beyond it: a `DepthOnly` pass whose
vertex stage displaces differently from its forward pass, clips alpha differently, or
skips the collapse to degenerate geometry passes this check and is culled from the frame
anyway. Nor does it reach D3's zero-intensity behaviour, or the MSAA a camera actually
renders with — it reads the sample count off the pipeline asset, which a camera can
override. D1 to D4 stay required render checks, and D1 is the one that catches a
mismatch.

| # | Path | Scenario | Expected |
|---|---|---|---|
| D1 | Both | Priming `Forced`, Forward+, moving heads under a cue | Every fixture body renders at every pan and tilt, no flicker and no partial geometry. A body that blinks out as it rotates is a depth pass not following the forward one |
| D2 | Both | Priming `Disabled`, same scene | Identical result, within 1.0% of pixels and 0.5 points of lit fraction; the differing pixels are edge scatter an `Equal` test leaves, one value of 255 or so. **The automated half reports `Inconclusive` when the two captures come back bit-identical**, because priming then never varied. It doesn't predict from MSAA or rendering mode whether priming will run, since those conditions live in the URP a project ships |
| D3 | Both | Priming `Forced`, a fixture faded to black | No hole in the scene. The fixture bodies have no collapse to degenerate geometry and always draw, which is correct for a housing. A custom opaque shader without depth passes is what `Validate Renderer Setup` reports |
| D4 | Both | Priming `Forced`, MSAA 4x | **Check which of two rows this is before reading it.** Stock URP declines to prime a multisampled camera, so `Forced` plus MSAA renders as though priming were `Disabled` and the row only shows that raising MSAA breaks nothing. A URP modified to prime anyway (marked `BASISVR MODIFIED` in `IsDepthPrimingEnabledRenderGraph`) makes it a genuine priming test; S13 says which a run hit |
| D5 | Both | A scene with no lights at all, of any type | Surfaces still light and beams still render. Depth comes from the pipeline; VRSL needs no light to obtain it |
| D6 | — | **Suite.** `VRSLDepthPolicyReport.LayerMaskVerdict` across five masks: a fixture layer excluded under priming `Forced`, the same mask with priming off, the layer included, no fixtures at all, and an unreadable mask | The first fails and **names the layer** — "a layer is excluded" leaves an author to find which of thirty-two. Priming off is reported without counting against the verdict, an including mask reports nothing, no fixtures reads `NOT CHECKED` rather than as a clean bill, and an unreadable mask reports instead of throwing or passing in silence. Automated because reaching the verdict through the menu item needs a real pipeline asset's mask, and this package does not change a project's renderer settings |

### Discoball

`VRSLDiscoballTests` in the suite covers K1, K2, K4 and K6, and `VRSLAudioLinkDiscoballTests` (AudioLink tests assembly) covers K5. K3 is by eye.

| # | Path | Scenario | Expected |
|---|---|---|---|
| K1 | D | A `Discoball` fixture from the shipped prefab on the rig, dimmer at full, spin at 90°/s | Its light record reads as the discoball type with no gobo slice, its spin axis is the fixture's up, it is lit, white tint reads white, and the spin phase advances by the authored rate between two reads a dozen frames apart (within a quarter). Proves the config, the compute branch and the clock, on the same path every reader uses |
| K2 | D | The same ball three metres over the rig's floor, movers dark, the surface prepass off so the floor lights as neutral grey; four frames: ball off, no cubemap, a cubemap black but for its downward face, the shipped cubemap | Judged as what the ball adds over the ball-off frame. No cubemap floods over 5% of the frame; the one-face cube and the shipped dots each light more than 0.2% and under 60% of that flood. The one-face cube proves the mask, the shipped asset proves its import. The prepass is off so the row judges the ball alone |
| K3 | — | The DMX example scene's discoball (sector 39, on) by eye, then `discoballBeams` on, then a coloured cubemap | Dots sweep the room about the ball's vertical at about 8°/s, and the ball turns with them, the same way at the same rate; with beams on the dots hang in the haze as thin shafts; a coloured cubemap tints them. The ball reflects the room as sharp facets where a reflection probe covers it. Cost: one more light in every tile inside its range, plus a cubemap fetch per raymarch step with beams on |
| K4 | D | The shipped prefab on the rig tilted 30°, dimmer at full, spin at 90°/s, twelve frames; then Spin Speed 0 for six | The ball (`lensTransform`) turns about the fixture's up by the authored angle (within a quarter), in the same sense as the light record's spin phase, without moving. At Spin Speed 0 it holds where it was rather than jumping to a new angle |
| K5 | A | The shipped AudioLink prefab tilted 30° beside the AudioLink manager, spin at 90°/s, twelve frames | The prefab's `lensTransform` is its sphere and the sphere is a mirror (as K6); the ball turns about the fixture's up by the authored angle (within a quarter), in the dots' sense, without moving |
| K6 | D | The shipped DMX prefab, not instantiated | Its `lensTransform` is its `Discoball-Sphere`, whose material is fully metallic and fully smooth with the facet normal map on |

### Volumetrics

`VRSLVolumetricLoopTests` in the suite covers V14 to V17. V11 to V13 want eyes and a
frame capture, and V11 reads its number off `VRSL Diagnostics`.

| # | Path | Scenario | Expected |
|---|---|---|---|
| V1 | — | `Half` (default) | Cones visible, silhouetted correctly against geometry |
| V3 | — | `Off` against `Standard` | Beams present at `Standard` and absent at `Off`, with surfaces still lit at both. Noise follows the level and is on wherever beams are |
| V4 | — | Beam a few metres from the camera, `Half`, with the geometry behind it first near and then far | Cone grain unchanged as the backing surface moves away. **Guards the per-light march span — a shared march loses sample density to whatever sits behind the beam** — and the adaptive step count, which is taken from the light's own span and must not grow or shrink with the surface behind the beam |
| V5 | — | Several cones overlapping, `Half` | Brightness in the overlap is the sum of the individual cones; no seam or banding where one cone's span ends |
| V6 | — | Camera inside a cone, then walking out through its edge | No pop or brightness step as the near end of the span crosses the camera. **Exercises the midpoint selection in `VRSL_NarrowSpanToCone`** |
| V7 | — | Narrow cone (small `spotAngle`), viewed side-on close to the fixture head | Smooth gradient, no dot-screen or weave pattern. **Worst case for span tightness — the cone is narrowest relative to its bounding sphere here** |
| V9 | — | `Standard` against `High` on a wide cone at long throw | `High` is smoother where stepping is visible at all. This is the case the level exists for: what governs stepping is metres of cone per step, so wide cones, long throws and dense haze show it first |
| V10 | — | Cone edges at a grazing angle | Soft feather to the outer angle, no hard rim. **A hard rim means the span is clipping before the attenuation has faded**, or that the visibility bound is skipping lights it should march. The bound is `VRSL_VOL_MIN_CONTRIB` in the volumetric shader; set it to 0 and re-run to tell the two apart |
| V11 | — | One fixture, walked away from the camera so its cone fills less and less of the view ray. `VRSL Diagnostics` on the manager at each distance | The cone keeps the same grain at every distance while the diagnostics' steps per light fall, never below 4 and never above the level's ceiling. Judged from that number, not by eye alone: the count is the span over the level's spacing, and a cone that fills less of the ray is a shorter span |
| V12 | — | A rig of fixtures all pointed away from the camera so only their falloff tails reach it, with `VRSL_VOL_MIN_CONTRIB` at its default and at 0 | Frame time falls at the default, with no visible difference in the image. `VRSL Diagnostics` reports a non-zero share of lights skipped as too faint to see. **If the image differs, the threshold is too eager** |
| V13 | — | A long beam through haze, camera moving along it | No visible repeat in the density structure. The baked noise texture wraps every 16 lattice cells, so a repeat would show at that spacing along the beam |
| V14 | — | `VRSLVolumetricLoopTests`: bake the field with the package's own compute and read it back | A 64-cubed repeat-wrapped texture whose texels span most of the range and sit around the middle, and whose texels across the wrap differ by no more than interior neighbours do. A non-periodic bake puts two unrelated lattice points at every wrap, about a third of the range apart, against a few hundredths between interior neighbours |
| V15 | — | `VRSLVolumetricLoopTests`: bake with a compute that has no kernel | The shared 1×1×1 white fallback, so density is unmodulated rather than zeroed, and one warning per session naming the kernel. Releasing a fallback leaves it alone |
| V16 | — | `VRSLVolumetricLoopTests`: collect the raymarch counters on the rig with its movers pointed straight down, at `Standard`, then at `High` | Steps per light marched never below 4 and never above the level's ceiling, and higher at `High` than at `Standard` on the same spans. The second half says the count follows the level's spacing rather than sitting at the ceiling |
| V17 | — | `VRSLVolumetricLoopTests`: the same rig, frozen and on the Ramp, with `volumetricIntensity` at `1e-12` | Every light that reaches the loop is skipped by the bound and no step is taken; the count of lights reaching the loop is unchanged from the lit frame, so the bound moved only the verdict. Restoring the intensity restores the march |

**A frozen capture pins the raymarch's clock as well.** The dither phase and the haze
scroll follow time by design, and at the step floor of four the dither reaches the
quantised output on a few grazing pixels. `FreezeForImageCapture` holds the clock at zero
through the manager's override, beside the strobe, the spin and the damping. A capture
that skips the freeze is comparing dither phases.

**Rows about what is in the air point the movers down.** Free-running, the rig's movers
follow their channels, which under the synthetic pattern points them off sideways at
shallow angles and puts almost no beam in frame. V16 and V17 switch pan and tilt off for
that reason, and image captures aim the beams down through `FreezeForImageCapture`.

**The counters are how V11, V12, V16 and V17 are judged.** `VRSL Diagnostics` prints
steps per light, lights marched per pixel and the share of lights skipped, and a sweep
row carries the same three. Collecting them is one atomic per counter per pixel, so the
diagnostic arms a request on its first call and answers on the next; the benchmark asks
after each timed window so the atomics never land inside a measured frame. A row that
reads a zero here should first ask whether a frame was collected at all.

### Cameras

`VRSLCameraPolicyTests` in the suite covers M5 and M7 to M11. M2 to M4 and the main-view
half of M5 want a mirror and eyes; M6 is a pair of player sweeps.

`secondaryCameraMode` is a policy with four values: `Match`, `Reduced` (the default),
`SurfaceOnly` and `Skip`. A camera that renders into a texture is a secondary camera to
the policy whatever it is for, which includes the test rig's own camera and the sweep's:
both register theirs as the player's view (`VRSLCameraFilter.RegisterMainView`), so every
row measures the level it is labelled with, and a row about the policy adds mirror cameras
of its own.

| # | Path | Scenario | Expected |
|---|---|---|---|
| M1 | D | Scene with a DMX screen reader | `Log Decoded Fixture Light Data` unaffected by fixture proximity to the reader quad. **Guards additive light corrupting the decode chain** |
| M2 | — | Mirror pointed at the rig, `secondaryCameraMode = Match` | Beams and lighting appear in the mirror, at the scene's level |
| M3 | — | `SurfaceOnly` | Surface lighting in the mirror, no volumetric cones |
| M4 | — | `Skip` | No VRSL lighting in the mirror |
| M5 | — | `Reduced` in a scene at `Standard` with a mirror pointed at the rig. By hand for the look, and `VRSLCameraPolicyTests.M5` in the suite on a mirror camera the rig adds | Beams appear in the mirror at `Low`, visibly cheaper than `Match`, with no missing fixtures and no difference in the main view. In the suite: the main view is pixel-identical across `Match` and `Reduced`; the mirror renders at `Low`; its frame differs from `SurfaceOnly` (the beams are there) and only slightly from `Match`; the raymarch counters from the mirror alone march the same lights at fewer steps each |
| M6 | — | The sweep player run with `-vrsl-mirrors`, compared with `CompareFromCommandLine` | 18 rows: 50 fixtures inside the cones at `Standard` and `High`, with none, one and three mirrors under each policy. Each row names the camera count, the policy and the level the mirrors rendered at. `Reduced` sits between `Match` and `SurfaceOnly` at both scene levels, and the cost per extra camera doesn't include a second decode. **Read the deltas and the ratios rather than the verdict** when the stored floor over-covers |
| M7 | D | `VRSLCameraPolicyTests.M7`: four cameras rendering every frame (the rig's and three secondary) for ten seconds of captured time, against the same rig with one | Every spinning fixture's phase advances at the rate its channel asks for, as N8 judges it, and the frame of light data (direction, spin, colour) matches the one-camera rig's to 1e-3. **An integrator that has drifted into a per-camera pass advances four times as far** |
| M8 | D | `VRSLCameraPolicyTests.M8`: a mirror toggled through main-only, mirror-only and both across 45 frames, under `Sweep` at a speed that moves every channel far each frame | On every frame the decoded colour equals the bytes the manager uploaded last, whichever cameras rendered, and the mirror's frame is not black on any frame it rendered. A one-frame-old colour is a decode that did not run for that frame's cameras; a black mirror is the grid textures unbound or the light path skipping it |
| M9 | — | `VRSLCameraPolicyTests.M9`: the camera filter as a function | `Match` renders everything at the scene's level; `SurfaceOnly` the same without beams; `Skip` nothing; `Reduced` everything one level below (`High` to `Standard`, `Standard` to `Low`, `Off` and `Low` unchanged). A camera with no target keeps the scene's level under every value. `Low` draws beams at fewer steps and wider spacing than `Standard` with no contact shadows, and is not in the sweep's level list. Reflection probes, registered data readers and cameras rendering into a texture the manager consumes stay skipped |
| M10 | D | `VRSLCameraPolicyTests.M10`: Z7's shader-driven fixtures seen from a mirror under `Skip`, rendered after the rig's view, with the rig's realtime fixtures on and with none | Each fixture's glow in the mirror with the realtime fixtures on is at least half of what it is with none. The mirror gets no VRSL lighting either way, so only the fixtures' own read of the grid differs. **A camera the manager leaves out of the grid's publishing reads the black the previous camera's graph left behind** |
| M11 | D | `VRSLCameraPolicyTests.M11`: the same fixtures seen from a registered DMX screen reader camera, rendered after the rig's view with the realtime fixtures on, against a mirror under `Skip` | Each fixture's glow in the reader is under a tenth of its glow in the mirror, which is clearly lit. **A reader handed the grid renders the fixtures' glow into the DMX input it feeds; a camera rendering into a texture the manager consumes is excluded the same way** |

**The rig holds depth priming per render** (`VRSLDMXRig.Build(priming:)`, Forced unless a
row asks otherwise, put back on dispose). The host rewrites the renderer's mode at a moment
of its own after start-up, so a row that set it once would measure whatever the host left;
D1_D2 passes its mode through.

**The decode runs once per frame, from the first camera VRSL renders.** Nothing in it
depends on the camera, so every camera in a frame reads one buffer; the DMX grid textures
are published from inside the graph, by an attachment-less pass, because a global
texture set outside it binds as black. M7 and M8 are the rows that catch it drifting into a
per-camera pass. The graph sets what that pass published back to black after each camera,
so it runs for every camera, including those the policy skips and frames with no realtime
fixtures; M10 and Z7 catch a camera or a frame left out. The one exception is a camera that
renders the DMX input, which M11 covers.

### DMX channel buffer

Values published as bytes rather than decoded from the pixel grid, read when the manager's
DMX Input is Stream data (the rig chooses it whenever it attaches its source). A grid input
reads the grid whether or not a source is attached, so N4 is the regression that matters most.

The buffer is indexed in VRSL's flat address space, where a universe occupies **520**
slots rather than 512: the grid is 13 wide, 512 does not divide by 13, and each universe
starts on a fresh row of 40. N6 and N7 exist because N1-N5 cannot see a stride mistake —
Ramp is a function of the flat index, so it reads the same whichever stride the source
packed with. Use the UniverseSlot pattern for those two rows; it is keyed on the slot
within a universe, which is the quantity that differs.

A source hands over `(universe, start slot, length)` blocks and the manager scatters them
into that flat space, so the stride is applied in one place and no source has to know
about it. Two things follow, and N13 and N14 check them. Values persist between frames,
because a block is a run rather than a whole universe and a partial snapshot has to leave
the slots it does not cover alone. And each universe's movement damping advances by the
show time its own blocks span, taken from the `age_us` they carry, rather than by the
frame delta — so a universe delivered every fourth frame settles at the same rate as one
delivered every frame.

| # | Path | Scenario | Expected |
|---|---|---|---|
| N1 | D | Synthetic DMX Channel Source in Ramp mode, Play, then `VRSL → URP → DMX Config → Validate DMX Channel Buffer` | PASS: every channel read back through the shader matches what was published. A constant offset in the reported channel is an indexing error; a value that looks like a neighbouring byte is a packing error |
| N2 | D | Same source in Fixtures mode, no video and no capture camera running | Fixtures **light** from the buffer alone: `Log Decoded Fixture Light Data` shows non-zero intensity and the buffer's colours. Colour, intensity, cone, gobo selection, movement, spin and strobe all read the buffer, so the fixtures light **and** move from it with no CRT chain involved |
| N3 | D | Fixtures mode on Stream data, then disable the source component mid-cue | The lights go **dark** within a frame: no latching at the buffer's last values and no fallback to a grid. Stream data is the event's choice, so with nothing sending it the lights are dark (E4 covers the message) |
| N4 | D | A grid input with a channel source attached (on standby), then the source detached, then Stream data, then the grid again | The grid decodes the same with the source on standby as with none, Stream data changes the decode, and the grid comes back to it exactly. Every existing scene reads a grid, so this is the regression that matters most |
| N5 | D | Source publishing fewer channels than a fixture is patched at — profiling scene, `universes = 1`, so 520 slots against a patch running to 638 | Sectors 40-49 read 0 and go dark, 10 fixtures. They read 0 rather than another fixture's values, which is the point of the row. Sector 39 goes dark too, for a different reason: it spans flat 508-520, so its colour channels sit in the inter-universe padding, which no block covers because no desk can address it. **`dir` is what separates the two**, since both read zero colour: sector 39's pan channel at flat 508 is a real published slot and gives it a direction of its own, while sectors 40-49 have no pan or tilt to read and must all share one. No real desk can patch a 13-channel fixture at sector 39 — it would straddle the 512 boundary — so treat it as a dead zone rather than a case to make work |
| N6 | D | Profiling scene, source in UniverseSlot mode with `universes = 2`. Compare fixture (000) at absolute channel 1 against fixture (040) at 521 | Both decode **identically** — rgb `0.027, 0.031, 0.035`. Legacy sector 40 is the first slot of the second universe, so it must read what sector 0 reads. Reading `0.059, 0.063, 0.067` instead means the source packed 512 to a universe and every fixture past the first universe is 8 channels early |
| N7 | D | Add one fixture with `useLegacySectorMode = false`, `dmxUniverse = 2`, `dmxChannel = 1`, same source and pattern | It decodes the same as fixture (040) above. Confirms the two addressing modes agree on where universe 2 begins, since `1 + 520` and `40 * 13 + 1` must both land on flat 521 |
| N9 | D | Profiling scene, channel source on Ramp, `universes = 4`, manager strobe left on Static. `Log Decoded Fixture Light Data` three or four times | Judge on the **`active` flag**, not on printed intensity: under Ramp these fixtures have low dimmers and print `intensity=0.00` while genuinely lit, because the log formats to two decimals. Fixtures whose strobe channel is at or below 0.2 must read `active=1` in **every** sample. The rest must read `active=0` in at least one sample **and** `active=1` in at least one — the second half separates a strobing fixture from one that is dark for some other reason. Which fixtures fall either side depends on the count, since the ramp value at `absChannel + 6` decides it: at 10 fixtures it is a single split, (000)-(003) held and (004)-(009) strobing; at 50 the pattern repeats three times as the ramp wraps at 251. At 50 fixtures the patch splits three ways and the medium and high buckets must **disagree** in at least one sample, which is what proves they run at different rates; ten fixtures cannot test it. Within a bucket every fixture must read identically at the same instant, wherever it sits on the truss. `active` also goes to 0 when a fixture is too dim to emit at all (`colorMax > 6/255`): a strobing fixture alternates, a too-dim one is constant, so repetition separates them |
| N10 | D | Same, then tick the manager's Disable Strobe (or press the control panel's global strobe toggle) | All 50 fixtures read non-zero intensity in every sample. The control panel's toggle sets the same Disable Strobe, so it reaches the compute and the fixture bodies too |
| N11 | D | Profiling scene with movers, channel source on Ramp, `universes = 4`. Log at any point after Play | Fixtures show **different** `dir` values from each other, tracking their own pan and tilt channels. The movement buffer is allocated zeroed, so had `AdvanceMovement` never dispatched every fixture would read pan and tilt of 0, and since the builder configures all 50 identically apart from sector they would share one direction. Two samples reading **identical** is the settled state and not a failure: with Ramp the target never moves |
| N12 | D | Set both `movementSmoothingMax` and `movementSmoothingMin` to 0.99, re-enter Play so the buffers zero, then log at about two seconds and again at thirty | How far each fixture has travelled follows its own smoothness channel, which is channel 13 of its sector — `absChannel + 12`. At the shipped defaults the time constants span only 0.14 s to 0.82 s and everything settles before a person can click twice; at 0.99 they span 0.17 s to 9.6 s. Expect (016), (017), (018), (036), (037) identical in both samples, and (000), (019), (038), (039) visibly different, with (038) still a tenth short even at thirty seconds. Restore the defaults afterwards |
| N8 | D | Profiling scene, channel source on Ramp, `universes = 4`. `Log Decoded Fixture Light Data` twice about ten seconds apart and compare the `spin` field | Every fixture's spin advances at `4 * (dmx > 0.5 ? dmx - 0.5 : dmx)` rad/s, negative above 0.5, wrapped to +-2pi. Solve the elapsed time from one slow fixture and the same figure must predict all the others, wraps included. Signs follow the direction bit with no timing needed: above 0.5 spins backwards. All spins reading 0.0000 usually means the fixtures have gobo spin disabled (`cfg.panSettings.w`), not that the integrator is dead |
| N13 | D | Profiling scene, channel source on Ramp, `universes = 4`, tick **Rotate Universes**. Let it run a second, then `VRSL → URP → DMX Config → Validate DMX Channel Buffer` and log the fixtures | PASS on every channel, and every fixture decoding the same colour it does without rotation. Judge on the decoded colour rather than on how many fixtures are lit: ch 248 sits below the emit gate and reads `active=0` throughout. Only one universe is published per frame, so this is the row that proves the manager keeps what it was last told: if the flat space were rebuilt each frame, three universes in four would read 0 and the fixtures above sector 39 would flicker dark. Validating in the first few frames warns rather than fails — the source has not been round the rotation yet |
| N14 | D | Three runs at `movementSmoothingMax` and `movementSmoothingMin` of 0.99, Ramp, `universes = 4`, each sampled at the same elapsed time mid-convergence. **A**: age 0, rotation off. **B**: age 200 ms, rotation off. **C**: age 0, rotation on | **B and C both reproduce A**, fixture for fixture. A constant age shifts a universe's clock at both ends of the subtraction and so changes no step at all, which makes B an exact repeat of A. Rotation gives a universe one step of four frames instead of four of one — the same total, because the damping and the CRT's pull are both contractions of the same error and contractions commute. Age used as the timestep would put B far ahead and fully settled; a step that advanced only one frame per arrival would leave C four times behind. The row also checks that something was still moving at the sample point: three settled runs agree whatever the step was. **Run this one through the suite**: hand-timing can't separate correct from incorrect in that window |
| N15 | D | `VRSLTrussDmxTests` in the suite. No Basis packages needed: the decoder is in the core assembly | The Truss record decoder against records built byte for byte the way Truss builds them: a full universe and a partial run round-trip with their offsets and ages; records accumulate and the arrays grow from below one block's worth; an empty snapshot is valid and appends nothing; a flipped value bit reads as `BadCrc` and leaves what was already appended untouched; each framing fault (`TooShort`, `BadMagic`, `UnsupportedVersion`, `Truncated`, a probe body as `BadPayloadMagic`, `PayloadTooShort`, `UnsupportedPayloadVersion`) is told apart; a block running past its payload refuses the whole record, intact earlier blocks included; CRC-32 matches the `123456789` check value |
| N16 | D | Synthetic DMX Channel Source on Ramp, `universes = 4`, Play, open the **DMX Monitor** | `Reading` shows `Stream data — VRSL_SyntheticDMXChannelSource`. In `Fixture` view every page shows the ramp climbing left to right and wrapping down the rows, the last 8 cells of the final row drawn as padding. Paging through all four universes shows the same pattern, since Ramp is a function of the flat address. Hovering any cell reports a channel one higher than the cell to its left |
| N17 | D | Same, then tick **Verify** | `Verify: all 520 channels read back as published`, on every page. This is N1 against whatever is really playing rather than against the Ramp pattern specifically — a mismatch reports the first differing channel, and a constant offset there is an indexing fault while a neighbouring-looking byte is a packing fault |
| N18 | D | Same, then disable the source component mid-cue | `Reading` switches to `Stream data — nothing is sending it, lights dark` within a frame, with a warning saying why, and the cells read zero; nothing falls back to the grid. Re-enabling switches it back. This is N3 read from the data rather than from the fixtures |
| N19 | D | Set the source's `universes` to 0 while it is registered | The warning says the source is attached but publishing no universes, so the lights are dark. Nothing else tells this apart from an empty scene: `ChannelCount` goes to 0 and the fixtures go dark with no error |
| N20 | D | Monitor open on a scene with no channel source at all, grid CRTs assigned and a DMX-over-video stream playing | `Reading` shows `Video grid — Horizontal` (the manager's layout), and `Grid` names the CRT and its dimensions with the universe count it holds: 3 for the shipped 26×240 Color/Intensity CRT, since a universe is 40 whole rows. Cells track the video. Judge the values against the fixtures rather than against the source: this path reads post-interpolation, which is what the fixtures read, not the raw grid bytes |
| N21 | D | Monitor open and visible, then dock it behind another tab and leave it for a minute | Sampling stops while it is hidden — no dispatch, no readback. Switching back to the tab resumes it with no interaction. Watch it in the profiler or with a frame debugger for the claim rather than the absence of a symptom; the window only asks to be repainted while it is being drawn |
| N22 | D | Overview view, synthetic source on Ramp with `universes = 4` and **Rotate Universes** ticked, then page through the four | Each universe's `last heard N s ago` cycles on its own rather than all four moving together — rotation publishes one universe per frame, so each is a few frames stale in turn. A universe is latched as its packet arrives, and DMX at 44 Hz does not divide into a frame grid. **Watching one go permanently stale needs a source that can stop a single universe, which the synthetic one cannot** — reducing its `universes` reallocates the flat space and drops the page instead. Do that half against a real desk feed |
| N23 | D | `VRSLBasisDmxOverVideoTests` in the suite, against `vrsl-dmx-marching.ts` | The record path against a fixture whose values say which frame they came from: channel c holds `(c - 1 + 5 * frame) % 251`, so the row recovers the offset from the values rather than assuming a frame, and every one of the 1560 channels must agree on it. Nothing dropped, three universes named. The offset walks back to a frame number (five is coprime with 251, so 201 inverts it) and that must be the frame the newest record's header names, or one behind it — the source decodes a record and the manager consumes its blocks on the following frame, so one is the settled state and two means a record went unread |
| N24 | D | Same suite, same fixture, no channel source: `BasisVideoRenderTextureOutput` frames the burnt-in grid strip into the RAW grid RT and the CRT chain decodes it | Every judged channel agrees on one offset, every sector's 13th channel included: each is read from its own cell (G17). The fixture's grid has channel 1 on the strip's top row, as a show's does, and the row frames it with `Corners.HorizontalStrip`, the corners the setup menu writes, moved up to where this strip sits. A reflection about the diagonal (each fixture's channels read backwards) or the component's identity default fails this row |
| N25 | D | Same suite, same fixture, **both** feeds present: the picture read on the Horizontal grid with the records on standby, then Stream data chosen and the records read the next frame | The two name the same frame, less the records that arrived between the reads, within two frames (the CRT's damping). The records are exact on every judged channel the frame after the switch, because standby kept draining them, and the picture is exact on every judged channel |
| N26 | D | Same suite, same fixture, picture only, read back from the RAW grid RT itself rather than through the accessor | Every channel holds its own value, within one DMX unit (the TV-range round trip). This is the row that tells a framing fault from a decode-chain one, which N24 cannot. The RT sits one frame ahead of the interpolation CRT, that being the CRT's own latency, so this row recovers its own offset rather than sharing one with N24 |
| N27 | D | `VRSLTrussOscDmxSourceTests.N27` in the suite. No Basis packages needed: records built byte for byte the way Truss builds them, sent to a `VRSLTrussOscDmxSource` on a free loopback port as the `/truss/dmx` message `truss-relay --osc` sends, one blob per message | After 30 records: nothing dropped, 30 datagrams received and none ignored, `UniverseCount` 4, and every one of the 2080 channels read back through the shader equals `RampValue` of its flat address; the newest header carries the lane's own sequence and frame index, equal, under carrier 6. A record naming universe 4 grows the count to 5 exactly once and all 2600 channels read the Ramp. A record with a value bit flipped after its CRC was written is counted dropped with `BadCrc` and the buffer still reads the Ramp. Then four datagrams that are not ours, a message under another address, one with an int argument, a bundle wrapping a real record, and three bytes that are not OSC, all count as ignored, none decodes and none counts as a drop |
| N28 | D | `VRSLTrussOscDmxSourceTests.N28` in the suite: the port already held by another socket when the source enables, then a source whose `listenAddress` is not an address | `Listening` false, `LastError` says the port is held by another program, one error logged naming the address and port, nothing received, and no exception out of enable. The bad address is reported as not an IP address, not as a held port. **By hand, the row that matters for a VJ:** `truss-relay --artnet --osc 127.0.0.1:12100` with a desk sending it Art-Net and nothing else running, a scene with a `VRSL_DMXURPLightManager`, fixtures patched, and a Truss OSC DMX Source added from `VRSL → URP → DMX Config`; press Play. The inspector's Live block shows datagrams and records climbing at the relay's `--osc-rate` and the fixtures follow the desk; the DMX Monitor's `Reading` shows `Stream data — VRSLTrussOscDmxSource`. Stop the relay and the counts stop; a second source on the same port reports the port held rather than fighting for it |
| N29 | D | `VRSLGridDecodeTests.N29`: the rig and the Horizontal flasher, light bar, 15-channel bar, 6x4 strobe and realtime blinder and par, each turned so the side it glows from faces the camera, lit from the synthetic source's Fixtures pattern held still over two universes with a grid of other values bound beside it; then lit from a grid carrying exactly the bytes the source published, with no source. Strobe held | The frames match. Bodies and the rig's mover heads on the URP include, the lens flares on the Built-in one, and the realtime light all read the buffer while a source publishes and the grid otherwise, from the one count the compute reads too. **Upright, these bodies glow away from the capture camera**, so a frame of them upright shows them black whatever they read |
| N30 | D | **What moves.** `VRSLGridDecodeTests.N30`: N29 for the laser (the Built-in include) and both realtime movers (the URP include), with the rig's movers behind them | The frames match. The laser's beams, movement and spin and the mover heads, which pan and tilt with their beams, follow the source. The row also guards when the grid is published: published after URP's prepasses, the heads would draw their depth and normals from a grid not yet bound |
| N31 | D | `VRSLGridDecodeTests.N31`, once per include: a test shader draws each include's channel, movement and spin-phase reads for every channel the rig patches, the source on Ramp over two universes with movement smoothing off; once from a grid carrying the manager's values and spin phases with no source, once from the buffers with a grid of other values bound | Every read returns what the manager holds, on both paths, in both includes, spin phase included |
| N32 | D | **The strobe gate.** `VRSLGridDecodeTests.N32`: a 13-channel light bar patched on sector 10 beside the rig's realtime mover on the same sector, strobing from the synthetic source's Ramp (its strobe channel reads 136, the fastest rate), 60 frames | On every frame the bar glows exactly when the realtime light is open. Both take the strobe clock from one sample per frame: the decode publishes the time it hands the compute, so the bar cannot strobe a frame behind the light whenever the frame renders |
| N33 | D | `VRSLGridDecodeTests.N33`: N29's and N30's scenes with a grid of other values bound, lit from the synthetic source on Stream data, the source component switched off two frames before the capture, against the same scenes on Stream data with no source; and the same switch-off on the Horizontal grid with the source on standby, against the scenes that never had a source | Both match. Under Stream data every fixture, shader-driven and realtime, is dark at once, with nothing falling back to the grid; on the grid the standby source going away changes nothing |
| N34 | D | **The compile half.** `VRSLShaderPlatformTests.N34`: every pass that includes the DMX read functions, in the six shaders that read the channel buffers (Opaque and the Opaque multi-channel bar, laser, lens flare, both mover bodies), compiled for Android on Vulkan with `ShaderData.Pass.CompileVariant`, plain, instanced, and instanced with multiview, without switching the editor's platform | All 36 variants compile, and each binds at least one of the four channel buffers: the shader model they need is kept on Vulkan rather than the buffer reads being compiled out. A mover's shadow pass binds the movement buffer alone, which is all its pose needs. The Opaque surface and the lens flare compile in their 13-channel mode, since they read DMX only under a channel-mode keyword. **Not the device half**: whether a Quest reads the buffers in the vertex stage needs a build and a headset |
| N35 | — | The standard 30-row player sweep, the package before shader-driven fixtures followed a source against after, each built from a clean checkout and captured three times, interleaved, on one machine, editor and host project | The whole frame with the manager on changes by less than the median run-to-run spread, and no row is slower beyond its spread; emitting fixtures match row for row. Build both sides from clean checkouts: a working tree whose decode textures are saved as Realtime (as test runs leave them) keeps the decode chain running with the manager off and makes every manager-off frame dearer |

### Runtime re-addressing

`VRSLReaddressTests` in the suite. A world script moves a fixture to another address by
setting its address fields and calling `ApplyAddress()` on the fixture, on either
`VRSLDMXRealtimeLight` or `VRSLDMXStatic`. Without the call a
realtime light's body keeps glowing to the old address, and its beam follows only once
something else makes the manager re-upload its fixture data. The par and blinder bodies
move with their beams too (J4).

| # | Path | Scenario | Expected |
|---|---|---|---|
| R1 | D | The rig on Ramp over two universes; one fixture moved from sector 3 to sector 10, another from sector 5 to universe 2 channel 1, each through `ApplyAddress()` and no manager call | From the second frame each beam decodes the colour of the fixture already at its new address, and every body renderer's property block carries the new `_DMXChannel` (131 and 521) |
| R2 | — | A Horizontal flasher (a `DMX_Static` with two renderers) moved to universe 2 channel 27, then to sector 3 in single-channel mode on channel 4 | Both renderers read `_DMXChannel` 547, then 44 |
| R3 | D | The rig on Ramp with strobe disabled, every fixture's `ApplyAddress()` called once to settle, then again with nothing changed | Colour and intensity bit-identical across the second call for all 50 fixtures; every body still on its own sector's channel |

### Fixtures switched on and off

`VRSLRuntimeEnableTests` (DMX, on the rig) and `VRSLAudioLinkRuntimeEnableTests` (AudioLink, plain
fixtures beside the rig, which only lends its camera) in the suite. Both managers follow their realtime
fixtures being switched on and off while the world runs: a fixture's `OnEnable` and `OnDisable` mark
the manager, which looks through the scene once at its next once-a-frame point (the DMX manager's
`LateUpdate`, the AudioLink manager's first camera of the frame) and rebuilds its buffers only if the
set changed. Presence is read from the light data by position, since a rebuild reorders the slots.
Z1 to Z6 run on both paths, under the same number.

| # | Path | Scenario | Expected |
|---|---|---|---|
| Z1 | D, A | A collected fixture's GameObject switched off, then on, with no call to `RefreshFixtures()` | The frame after the switch off the manager holds one fixture fewer and its light data has nothing at the fixture's position; the frame after the switch on, both are back |
| Z2 | D, A | The same with the fixture under a parent, the parent switched | As Z1 |
| Z3 | D, A | Seventy extra fixtures under one parent, switched on with it; then all seventy switched off one by one in a single frame, then on one by one in a single frame | The manager holds them after the parent comes on; each single-frame switch costs one scan and one rebuild, and the frames after it neither |
| Z4 | D, A | A manager with one fixture; the fixture switched off, then on | With it off the manager holds no fixtures, no light data and no fixture configs; with it on it is drawn again |
| Z5 | D, A | Ten frames with nothing switched; on the DMX path, then a fixture moved to a new address with `ApplyAddress()` | No scan and no rebuild in those frames, and none for the new address |
| Z6 | D, A | A collected fixture's component switched off on a GameObject left on, then on | As Z1: a switched-off component casts no light. Its body keeps glowing with the show, since the body reads DMX or AudioLink itself |
| Z7 | D | A flasher, a light bar, the 15-channel bar and the 6x4 strobe in front of the rig on a bright grid and on a black one, Horizontal and Vertical: with the rig's realtime fixtures on throughout, never on, and on for four frames then all switched off for twenty | Each shader-driven fixture glows (its mean brightness over its own pixels, bright grid less black) with the realtime fixtures on and in the scene that never had one, and its glow with every realtime fixture switched off is at least half of the latter. **Render Graph sets the grid back to black after each camera that published it, so a manager that stops publishing at zero realtime fixtures leaves them dark** |

Nothing here runs in edit mode: neither realtime light nor either manager runs in the editor outside
Play, so Unity never calls the hooks there, and placing or removing fixtures in a scene, in Prefab
Mode or through the migration tool is unchanged.

### Par and blinder bodies

`VRSLParBlinderBodyTests` in the suite. Every DMX par and blinder lists its body, and a blinder
its lens flare, in `fixtureShellRenderers`, so the realtime light drives them from the
fixture's address with tint 2; the 5-channel ones set `use5ChannelMode`. A renderer left off
the list glows at full whatever the desk sends, which is what to suspect when a par lens stays
lit at black.

| # | Path | Scenario | Expected |
|---|---|---|---|
| J1 | D | The four DMX par and blinder prefabs (13- and 5-channel), as assets and once instantiated | Every renderer is in `fixtureShellRenderers`, the tint is (2, 2, 2), `use5ChannelMode` is set on exactly the 5-channel ones; on wake each listed renderer's block carries `_EnableDMX` 1, the fixture's `_DMXChannel` and the tint |
| J2 | D | The 13- and 5-channel par and blinder, turned to the camera, strobe off and their cast light off (its beam points at the camera in the same colour), every channel at 0 and then dimmer and red at full; read from a painted Horizontal grid and from a channel source; measured on a half-float capture, since a lit body is far above 1 in every channel and clips to white in 8 bits | At 0 every body and flare is near black; lit, they are bright with red leading green and blue many times over, the same from either input |
| J3 | D | The 5-channel par and blinder on Ramp, one universe | Each decodes its colour from +1 to +3 of its address |
| J4 | D | A Horizontal 13-channel par, set up as in J2, moved from sector 3 (all 0) to sector 10 (dimmer and red full) through `ApplyAddress()` | The body renders dark before the call and red after it, its block carries channel 131, and the beam decodes sector 10's red |
| J5 | D | **By eye.** A par and a blinder in the editor, body and flare, at black, half, full and strobing (the synthetic source's Sweep drives every channel through its range) | The glow reads as a lamp at tint 2, sits dark at black, and the flare reads well at its curve and tint |
| J6 | — | A cube on each fixture body shader (the AudioLink static body, the DMX surface body and multi-channel bar body, and in a row above them the four mover bodies, DMX and AudioLink, spot and wash, on cubes with black vertex colours), emission off, beside a cube on URP Lit, under a white directional light within the shadow distance; captured with the light off, then on at one shadow cascade, at four, and casting no shadows, reading back which main-light shadow keyword URP set for each | Each lit capture is measured as its gain over the light-off one, so only the light's own contribution counts. The URP Lit cube gains in every capture, or the light or camera is wrong. The one-cascade capture ran `_MAIN_LIGHT_SHADOWS` and the four-cascade one `_MAIN_LIGHT_SHADOWS_CASCADE`, or the row didn't exercise the variants it is for. Each body gains in all three, at four cascades and without shadows at least half its own one-cascade gain: lighting gated on `_MAIN_LIGHT_SHADOWS` goes black at four. On a Forward+ renderer a body that gains nothing at one cascade beside a lit URP Lit cube means the shader isn't built with `_CLUSTER_LIGHT_LOOP`, which costs it the main light as well as the additional ones. A mover body lit through the Built-in pipeline's light variables, which URP never sets, gains nothing in any capture. Inconclusive where the pipeline asset doesn't support main-light shadows, its shadow distance doesn't reach the cubes, the URP Lit cube doesn't gain, or either keyword is wrong |
| J7 | — | A quad on a copy of each shipped blinder flare material (AudioLink, and DMX Horizontal), lit red with AudioLink and DMX off, 5 m from the camera beside a cube on URP Unlit; captured with the flares shown and then hidden, with no fog and then under green linear (0 to 10 m), exponential (density 0.14) and exponential squared (0.17) fog; measured on a half-float capture | The difference between the shown and hidden captures is what each flare adds, and it is the same under every fog as without: neither dimmed nor tinted green. The Unlit cube turns towards green under each fog, or the fog never reached the frame and the row is inconclusive. A flare that adds the fog's colour instead of its own is applying fog from a fog coordinate its vertex never wrote |
| J8 | — | Each blinder flare shader (AudioLink, and DMX Horizontal) on a copy of its shipped material and on a material made from the shader with only its texture and a red colour set, lit with AudioLink and DMX off, 5 m from the camera; a cube on URP Lit set 1 m in front of each flare on the line to the camera, then moved aside; captured with the flares shown and then hidden; measured on a half-float capture | In the open every flare adds light; behind its cube every flare adds none. A flare draws over the scene whatever its depth, so it hides only by its own test against the camera's depth. A flare that adds the same light blocked as in the open isn't running that test |
| J9 | — | The same cubes as J6 under a white point light in front of them and no sun, captured with the light off and on for each of three paths: Forward+, Forward with additional lights per pixel, and Forward with them per vertex (how Basis's Quest renderer is set). The row sets every Universal Renderer's rendering mode and the pipeline asset's additional-light mode for each capture and puts them back after, reading back which additional-light keyword URP set | Each lit capture is measured as its gain over the light-off one on the same path. The URP Lit cube gains on every path, or the light or camera is wrong. The Forward+ capture ran `_CLUSTER_LIGHT_LOOP`, the per-pixel one `_ADDITIONAL_LIGHTS` and the per-vertex one `_ADDITIONAL_LIGHTS_VERTEX`, or the row didn't exercise the path it is for. Each body gains on all three. Forward+ defines `_ADDITIONAL_LIGHTS` itself under the cluster light loop, so a body built without the additional-light variants is lit there and dark on plain Forward; one built with them that never hands URP its vertex lighting is dark per vertex only. Inconclusive where the URP Lit cube doesn't gain or a keyword is wrong |
| J10 | — | A sphere on each fixture body shader with a metallic-smoothness map (the DMX surface body and multi-channel bar body, and the four mover bodies on spheres with black vertex colours), Metallic 0 and smoothness 0.8, beside a sphere on URP Lit, under a white directional light; captured with three maps on every body: red 0 alpha 1, red 1 alpha 1, red 0 alpha 0, URP Lit given each map's alpha as its smoothness; measured on a half-float capture | With Metallic at 0 the map's red can change nothing, so each body looks the same with the first two maps, and with the third, a rough surface, it loses its highlight. A body that only lights its highlight when the red is 1 reads smoothness from the metallic channel. Inconclusive where URP Lit shows no highlight at smoothness 0.8 |
| J11 | — | The AudioLink laser's mesh on a copy of its shipped material, scroll 0, in front of the camera, fed an AudioLink texture the row writes (band 0 at a chosen amplitude and the other bands silent, the material on band 0, theme colours red, green, blue and yellow, a blue colour chord light); red emission with AudioLink off and the band at 0.03, then AudioLink on at amplitude 1, 0.5, 0.051 and 0.049; white emission on theme colours 2 and 3; red emission on the colour chord; white emission sampling a magenta texture. Each capture is the frame with the laser shown less the frame with it hidden, summed, on a half-float capture | With AudioLink off the laser adds red although the band is below the threshold, so the band is read only when AudioLink is on; on at full amplitude it adds red too. At half amplitude it adds less than at full. Just above the 0.05 threshold (0.051) it adds red, and just below it (0.049) nothing, so a cutoff moved by more than 0.001 fails; a laser without the threshold would add most of the 0.051 result at 0.049. On theme colour 2 it adds green and on 3 blue (AudioLink's second and third), on the colour chord blue whatever its emission, and sampling the magenta texture magenta. A laser that ignores the amplitude, reads another band, cuts off anywhere but 0.05, applies it with AudioLink off, or colours only from its emission, fails here |
| J12 | — | A quad on a copy of each shipped blinder flare material (AudioLink, and DMX Horizontal) with its light source radius at 1 m, lit red with AudioLink and DMX off, 5 m from the camera; a cube on URP Lit 1 m in front of each flare covering its occlusion grid's columns right of the 4th of 17, then right of the 11th, then its rows above the 6th, the cube's edge halfway between two columns or rows as the camera sees them; captured with the flares shown and then hidden, red less green, on a half-float capture | Each flare in the open adds red. With part of its grid covered it keeps 4/17, 11/17 and 6/17 of that, within a quarter of a column. A flare whose test points sit at the wrong spacing along either axis dims by the wrong share |

### Basis video → DMX (optional integration)

Needs `com.basis.mediaplayer` in the project; without it the assembly is skipped and none of
these rows apply. `com.cnlohr.cilbox` is optional: with it the components are `[Cilboxable]`
and usable from sandboxed world scripts, without it they are plain components for a
scene-owned player.

| # | Path | Scenario | Expected |
|---|---|---|---|
| B1 | D | `BasisVideoToVRSLDMX` with `GlobalTextureName` set to the RAW grid global, DMX-over-video stream playing | Fixtures decode as they do from a capture camera; `Log Decoded Fixture Light Data` shows non-zero intensity |
| B2 | D | `BasisVideoRenderTextureOutput` into the RAW grid RT, corners dragged over the live frame | Framed grid lands in the RT and the inspector's output preview matches what the decode chain reads |
| B3 | D | Stop the player mid-cue, `ClearWhenNoFrame` on | Fixtures go dark rather than latching the last grid |
| B4 | D | Two clients, one reporting `OutputFrameIsTopLeftOrigin` | Grid decodes the same way up on both. A vertical flip on one means the per-client origin correction |
| B5 | D | Change stream resolution or re-open mid-cue, so the player reallocates `OutputTexture` | Decode continues; the global is republished on the new texture rather than left on the dead one |
| B6 | — | Project without `com.basis.mediaplayer` | `Towneh.VRSL.URP.Basis` and its test assembly skipped on their define constraint, no compile errors, rest of the package unaffected |
| B6a | — | Project with `com.basis.mediaplayer` but without `com.cnlohr.cilbox` | The three integration components compile and work on a scene-owned player, without the `[Cilboxable]` attribute; with Cilbox added they carry it |
| B7 | D | `VRSLBasisUserDataTests.B7_B8` in the suite: a real `BasisMediaPlayer` plays `truss-dmx-ramp.ts` (300 frames, one Truss DMX record per frame carrying VRSL's Ramp, universes 0-3 from the start), `BasisUserDataToVRSLDMX` with `minimumUniverses = 4` feeds the manager | After 30 records: nothing dropped, `UniverseCount` 4, and every one of the 2080 channels read back through the shader equals `RampValue` of its flat address; after all 300: still nothing dropped, the header's frame index and sequence agree and track the frame. A count short of 300 at `Ended` is a lost record, and the first place to look is the hand-over between the player's drain and a subscriber that attached a frame late |
| B8 | D | Same row, past frame 90, where the stream starts naming universe 4 | `UniverseCount` grows to 5 exactly once and all 2600 channels read the Ramp, the new universe included; it does not shrink again |
| B9 | D | `VRSLBasisUserDataTests.B9` in the suite: `truss-dmx-ramp-damaged.ts`, every tenth record with a value bit flipped after its CRC was written | 30 of 300 dropped, `BadCrc` observed as the reason while playing, and the buffer still byte-exact from the 270 that arrived intact |
| B10 | D | `VRSLBasisUserDataTests.B10_*` in the suite: `truss-dmx-ramp-transcoded.ts` (the intact fixture re-encoded with x264) and `truss-dmx-ramp-stripped.ts` (remuxed through `filter_units=remove_types=6`) | Both: the picture plays, `RecordsDecoded` and `RecordsDropped` stay at 0, `LastResult` stays `Ok`, `UniverseCount` stays at `minimumUniverses`. The lane is absent, not damaged, and nothing says otherwise. A rewritten-SEI path reads as B9 does (`BadCrc`/`BadMagic`, dropped count climbing). **By hand, and the part that matters for a show:** the same check against the real CDN you mean to use, with `logDrops` on; a provider that transcodes gives you the B10 reading and is not usable for this |
| B11 | D | `VRSLBasisUserDataTests.B11` in the suite, run only with `VRSL_TRUSS_LIVE_URL` set: a live lane (Art-Net sender → `truss-relay --artnet` → `mediamtx` → RTSP) carrying the same Ramp over five universes | Over five seconds the record rate sits at the video's frame rate (20-45/s accepted), nothing is dropped on a remuxing path, `UniverseCount` is 5 and all 2600 channels read the Ramp. Same assertions as the fixture rows, so the two agree or the difference is the path |
| B12 | D | `VRSLBasisVideoFramingTests.B12` in the suite: the screen and reader camera of VRSL's shipped Horizontal video reader (`VRSL-DMX-USharpVideoReaderScreen-Horizontal`: the stock screen mesh, two units across with U running against x, turned half round y at scale 5; the camera rolled a quarter turn at orthographic size 9.6 into a 104x960 grid; both under a parent scaled 1.92 x 1.08), then the camera widened past the screen's edges, then turned away from the screen, then facing it again with the mesh's Read/Write off | **Bake Framing From Camera** reads the mesh's own UVs: the shipped framing bakes to the corners worked out from the prefab's values, channel 1 on the strip's top row, within a pixel of a 1080p frame of the setup menu's corners; framing wider carries on past the screen's edges in proportion. Turned away, or with a mesh Play mode can't read, the bake refuses, logging nothing. A bake that assumes a one-unit quad fails the first two |

### AudioLink (optional)

The AudioLink side compiles into `Towneh.VRSL.URP.AudioLink` and `Towneh.VRSL.URP.AudioLink.Editor`
only when `com.llealloo.audiolink` 3.1.2 or newer is installed (an older AudioLink leaves them
out, as no AudioLink does), and the package doesn't depend on it. Y1 to Y3 and Y7 are install
checks in a throwaway project (`-createProject`, the package by `file:` path or from a release
tag), since a host project usually carries AudioLink; Y4 to Y6 are in the AudioLink test
assembly. A scene inside a package installed from git is read-only and can't be opened by
script, so a check that opens the example scenes opens copies of them.

| # | Path | Scenario | Expected |
|---|---|---|---|
| Y1 | D | An empty project with no AudioLink, the package added by `file:` path or from its tag | No `error CS`; `Towneh.VRSL.URP` and `.Editor` built, the AudioLink assemblies skipped on their constraint; nothing in the Console at import, and every package asset re-imported with none; all shaders compile; the DMX example scene opens clean. The core suite runs in that project too (the test framework, the package as a testable and a URP pipeline asset added), with no failure that the same project with AudioLink added doesn't also show: a bare project's gamma colour space, MSAA 1x and unsupported GPU Resident Drawer fail N30, J2, J4, S15 and S16 either way. Opening the AudioLink example scene reports its missing AudioLink prefabs; that scene needs AudioLink to run |
| Y2 | A | AudioLink 3.1.2 added to the Y1 project from its git URL | The AudioLink assemblies build; the AudioLink fixture prefabs, the manager prefab and the example scene carry no missing VRSL component; the AudioLink example scene lights to audio, by eye. AudioLink's own `AudioLink` and `AudioLinkControllerV0` prefabs carry VRChat components (`UdonBehaviour`, its UI shape, `GlobalSlider`) that don't load outside VRChat; they are AudioLink's |
| Y3 | A | An AudioLink realtime light with `globalIntensity` 0.37 saved in a scene of the Y1 project; AudioLink removed, the project opened; AudioLink added back | Without AudioLink the project opens and compiles (no Safe Mode) and the fixture's component shows as missing; with AudioLink back it is the AudioLink realtime light again at 0.37 |
| Y4 | A | `VRSLAudioLinkIntegrationTests.Y4`: every declaration in `VRSL-AudioLink-Data.hlsl`, comments and spacing aside, against the installed `AudioLink.cginc` | Each is a line of AudioLink's file, the six `ALPASS_*` positions among them, so an AudioLink release that moved a row fails here instead of lighting the wrong band |
| Y5 | — | `VRSLAudioLinkIntegrationTests.Y5`: the benchmark's pass markers against the AudioLink manager's `DecodeMarker` | Listed, since the benchmark names it as text |
| Y6 | A | `VRSLAudioLinkIntegrationTests.Y6`: the control panel beside a switched-off AudioLink manager (light 0.8, beams 0.6, `High`) and an AudioLink discoball at 0.5, sliders at a half, a quarter and a half, then Off and a sample texture | The panel finds them through the light paths the AudioLink assembly registers: light 0.4, beams 0.15, discoball 0.25, quality Off, the manager holding the sample texture. Resolving the managers again keeps the authored values rather than recapturing scaled ones |
| Y7 | — | The Y1 project with AcChosen's package and an upstream AudioLink mover beside a DMX one; **VRSL → URP → Migrate Scene Fixtures**, then AudioLink added and the tool run again. AcChosen's package depends on AudioLink itself and needs UdonSharp to compile, so the row takes that dependency out of its `package.json` and adds stand-ins for UdonSharp and the VRChat SDK, as L4 does | The DMX fixture migrates; the AudioLink one stays where it is and the summary lists it under "AudioLink fixtures left as they are, because AudioLink isn't installed. Install AudioLink, then run this again". With AudioLink added, both migrate |
| Y8 | A | **Hand row.** Analyse This Scene over the AudioLink example scene, in a project with AudioLink | Reports the AudioLink path, its fixtures and its cost |

### VR (single-pass instanced)

| # | Path | Scenario | Expected |
|---|---|---|---|
| X1 | — | Any lit scene in headset | Lighting identical in both eyes |
| X2 | — | Volumetric cones in headset | Cones in the same world position in both eyes |
| X3 | — | Diagnostics, tile culling active | No vertical mirroring of lighting between eyes. Mirroring points at the `renderIntoTexture` inference in `VRSLTileCullPass`; clearing `lightCullShader` isolates it |
| X4 | — | Fixture body meshes in headset. **Suite half** (`VRSLBasisStereoTests`, Mock HMD): a cube per body shader beside a white URP Lit cube, in three groups framed one at a time, each body in its own colour: the static bodies (AudioLink static red, DMX surface green, multi-channel bar blue), the DMX movers (spot red, wash green) and the AudioLink movers (spot red, wash green). The movers' cubes carry black vertex colours, which the movers draw in place. Rendered single-pass instanced on Basis's desktop and Android pipeline assets in turn, both eyes read back from the display's eye texture | Visible in both eyes. Suite half: each body covers at least 200 pixels of its colour in each eye, the two eyes within a factor of two. Inconclusive if the display didn't hand back a two-slice eye texture, URP Lit isn't seen in both eyes, or the backend can't copy an array slice into a 2D texture (`CopyTextureSupport.DifferentTypes`), which the readback needs. The Android asset renders on the editor's GPU, so the suite half checks the Quest renderer's configuration, not the headset's GPU; the headset half stays. Mock HMD hands back a single-sample eye texture even where the asset asks for MSAA, so the readback's multisampled path is checked on its own: a 2x multisampled two-slice array cleared red and green reads back red and green |
| X5 | — | A blinder or flasher lens flare, a laser and the discoball in headset, DMX and AudioLink prefabs | Each visible in both eyes at the same world position, the flare quad facing each eye |

### Platforms

`VRSLShaderPlatformTests` in the suite. Basis ships Windows (Direct3D 11 and 12), Android for
Quest (Vulkan) and desktop Linux (Vulkan); the suite and the headless shader validation run on
the editor's own API, so these rows compile every shader in the package for the other two with
`ShaderData.Pass.CompileVariant`, without switching the editor's platform. Each pass compiles
with the keywords it always has on (the first of any list declared without an empty entry), so
no variant that can't exist is built. A pass borrowed from URP's Lit with `UsePass` can't be
compiled through the shader that borrows it; Unity reports it as a UsePass by subshader and
pass index, and only a pass Unity names that way is left to URP and listed. Any other error,
returned or logged, fails the row, and so does a run that compiles no program at all. Compute
shaders aren't covered: Unity has no public
call to compile one for another platform without switching, so a build for the target is still
what proves them.

| # | Path | Scenario | Expected |
|---|---|---|---|
| T1 | — | Every shader in the package, every pass of the subshaders URP uses, vertex and fragment programs, compiled for Android on Vulkan: plain, with `STEREO_MULTIVIEW_ON` (how a Quest renders both eyes in one pass) and with `INSTANCING_ON` | Every program compiles. The log names each borrowed pass left to URP |
| T2 | — | The same for desktop Linux on Vulkan: plain and with `INSTANCING_ON`. Linux runs in desktop mode, so there is no stereo variant | Every program compiles |
| T3 | — | Every shader in the package, by the name it declares | No two share a name; `Shader.Find` on a shared name returns whichever Unity loaded first. A failure names the shared name and the files declaring it |
| T4 | — | Every shader in the package, read from its file for a `CustomEditor` line, against the material inspector classes the project defines | Each inspector a shader names exists and Unity can create it: not abstract, not an open generic. Unity shows its default inspector for a name it can't find or create, so nothing else fails. A failure names the shader file and the missing class |
| T5 | — | Every `.shader`, `.hlsl` and `.cginc` file in the package, read from source, for a varyings struct carrying both `UNITY_VERTEX_INPUT_INSTANCE_ID` and `UNITY_VERTEX_OUTPUT_STEREO` | Each such file calls `UNITY_TRANSFER_INSTANCE_ID`. Without it the fragment's `UNITY_SETUP_INSTANCE_ID` reads id 0, which under single-pass instanced also sets the eye index to 0, and no render shows it until a fragment reads something per eye or per instance. Checked per file, not per vertex function. A failure names the file and the struct |

---

## Known gaps — not bugs

Don't raise these; they're documented limitations with reasons in
the [Architecture](https://github.com/towneh/vrsl-urp/wiki/Architecture) page of the wiki.

- Light doesn't stop at walls: there are contact shadows only, no light-perspective shadow maps.
- Occluders off screen cast no shadow, and shadows fade as an occluder leaves frame.
- Metallic-smoothness maps are read under Unity's own property names (`_MetallicGlossMap`,
  as URP's Lit uses). A shader that names its maps otherwise is read from its scalar
  smoothness and metallic only, so a glossy map region can light flatter or glossier than
  it renders.
- No ambient occlusion term. AO models indirect occlusion and would be wrong applied to
  direct light.
- Transparent geometry receives no VRSL light: the passes run after opaques.
- Running the DMX and AudioLink managers on the same camera is unsupported; both write
  `_VRSLLights` and the last one scheduled wins.
---

## Reporting

State the row IDs exercised and their outcome, plus platform and whether it was desktop or
headset. Rows not run should be named as such rather than left implied.

When a change alters the feature surface — a new capability, a removed one, a changed
expectation — update this file in the same branch.
