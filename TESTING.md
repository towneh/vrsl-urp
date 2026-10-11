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
includes anything structural.

### 2. The DMX suite

Most rows below are PlayMode tests, and each section names its test classes. Run them
rather than performing them by hand:

```
Unity.exe -batchmode -projectPath <project> -runTests -testPlatform PlayMode \
  -assemblyNames "Towneh.VRSL.URP.Tests;Towneh.VRSL.URP.Basis.Tests" \
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

The rows are still written out in full below, because they are the specification the tests
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
| C6 | — | `VRSLManagerLifecycleTests` in the suite: a manager switched off in the inspector loads beside a running one | The running one owns the singleton and drives the light path. A switched-off component still gets `Awake` but never `OnEnable` or `OnDisable`, so a claim made there is never released and the running manager destroys itself as a duplicate — a scene with a manager in it, no lighting, and nothing in the Console |
| C7 | — | `VRSLManagerLifecycleTests` in the suite: two managers, one of them switched on after the other already owns the singleton, then the owner switched off, with a channel source attached | The one still running takes the singleton over, the channel source comes with it, and the light path keeps going. A manager that stood down on being enabled gets no second `OnEnable`, so without a handover the scene is left with an enabled manager, no owner and no lighting — the same silent symptom as C6, reached from the other direction. The source has to travel too: it published to whichever manager held the singleton, and a new owner without one stops publishing on its first frame |
| C8 | — | `VRSLManagerLifecycleTests` in the suite: a second manager left switched on beside the one that owns the singleton | The owner's channel-count global survives. Per-frame work is the owner's alone: a non-owner reaching `UploadChannels` with no source of its own rewrites the shader globals, and `AdvanceState` sets parameters on the compute asset both managers share and dispatches its kernels — so which `LateUpdate` ran last decides what stood. The row reads the global because it is cheap and unambiguous, **not because anything consumes it**: nothing in the package reads that global today, the compute being bound per dispatch and the render pass setting the count on its own command buffer. **The compute-asset half is the part that could actually bite and has no row** |

### Surface accuracy

`VRSLSurfacePrepassTests` in the suite covers S10 to S16. S1 to S9 are judged by eye.

**Where the normals come from decides which passes are in the frame.** Wherever URP's
own depth-normals prepass can be drawn, the manager asks URP for it and VRSL's normals
draw does not run: a frame capture then shows URP's `DepthNormals` prepass and no
`VRSL Normals Prepass`, and the lighting is the same to the pixel (S11). URP cannot
draw it on a camera that is both multisampled and depth primed, and on a URP that
primes with MSAA the request itself fails the whole frame (S13), so there VRSL draws
its own as before. `VRSL → URP → Validate Renderer Setup` says which each camera will
get and why; `VRSL Diagnostics` on a manager says which the last camera got.

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
| S10 | — | **Suite.** The rig at MSAA 1 and MSAA 4 with priming Disabled, where URP's normals are read at both, and a third capture at 4x with `forceOwnNormals` | Surface shading matches across MSAA: the lit area within half a point and the mean lit colour within 2 of 255, since multisampling moves every edge and pixels are not compared. The two 4x captures, which differ only in where the normals came from, agree to the pixel. Priming Disabled on purpose: under Forced the 4x frame is S14's fault, not the sample count's. **Passed 2026-09-03**: lit 62.76% against 62.82%, lit mean (44.8, 46.3, 22.5) against (44.8, 46.3, 22.6), 1.2% of pixels differing between 1x and 4x, and the two 4x captures 0 px apart. |
| S11 | — | **Suite.** MSAA 1, priming Forced, the policy left to decide and then overridden with `forceOwnNormals` | With the policy, the prepass reads URP's texture on every render and draws no normals of its own; forced, the reverse; and the two frames agree. **Passed 2026-09-03**: own draws 0 / URP reads 60 against 60 / 0, frames bit-identical, 0 px differing. The pass list itself is the hand half: a frame capture with the policy engaged shows no `VRSL Normals Prepass` and URP's `DepthNormals` prepass in place of its `DepthOnly` one |
| S12 | — | **Suite.** The rig's floor moved to a layer left out of `prepassLayers`, against the same mask leaving out a layer nothing is on | The floor still lights, as neutral grey rather than black, and geometry inside the mask is untouched. **Passed 2026-09-03**: lit area unchanged at 62.8%, 8.3% of pixels changed with the floor left out (max 0.77), and leaving out the empty layer changed 0 px |
| S14 | — | **Suite.** Volumetrics off, priming Forced: the floor lit at MSAA 1 and at MSAA 2, the sample count Basis ships, against the 2x frame with the floor left out of the prepass | The floor shades at 2x as it does at 1x, not as the neutral fallback. **Ignored in the suite until the gate is fixed** (`[Ignore]` naming the fault), so a run stays green and a new failure in the file is not lost among a known one; remove the attribute to run it, and it fails. **Measured 2026-09-03**: lit mean (11.4, 15.4, 19.8) at 1x, (30.0, 32.9, 22.0) at 2x, and (30.0, 32.9, 22.0) at 2x with the floor on the fallback, 8.9% of pixels differing between 1x and 2x. Under priming with MSAA `_CameraDepthTexture` is a resolve of the multisampled depth, which takes the farthest sample; at a grazing angle that is centimetres from the depth the prepass rasterised at the pixel centre, and `VRSL_SurfaceDataCovers` allows 0.05% of eye depth, so it rejects the surface and the lighting pass falls back to mid-grey. Confirmed by widening the tolerance to 2% for one run, which brought 1x and 2x to 0.25% of pixels apart. Rows run at MSAA 1 never see it: there the depth prepass writes the camera depth texture directly and the gate holds. Not fixed yet: the gate is what S8 and S9 guard, and a change to it wants those re-run on avatars |
| S15 | — | **Suite.** The rig's floor on a URP Lit material of known colour, gloss and metallic, captured through the prepass targets with the GPU Resident Drawer Disabled and then on, the mode held on the pipeline asset per render | Disabled, the prepass reads the material back as authored (the control). On, the floor is not captured at all and the frame still lights it, as the neutral fallback. Inconclusive if the drawer did not come up with the mode set, naming the project's reason. **Passed 2026-09-04**: albedo (154, 55, 8) against (154.0, 54.6, 8.4) authored, smoothness 76 of 76.5, metallic 64 of 63.8, 164409 of 164409 floor pixels captured; on, 0 of 164409 captured, frame lit 62.72% either way at mean (66.0, 64.0, 53.4) against (66.3, 57.6, 52.4). **On rc.3 the batched floor read albedo (8.7, 8.4, 9.7), smoothness 255.6, metallic 0**, and blue emission on the material came back in the albedo, which is how the mechanism was pinned: the drawer draws its batches through the floor's own forward pass whatever the override says. |
| S16 | — | **Suite.** The rig's floor on URP Lit with a one-colour metallic map (metallic in red, smoothness in alpha) and scalars that disagree with it, captured three ways: the map keyword on; the keyword off with the map still assigned; the keyword on with smoothness taken from the base map's alpha | Keyword on: metallic from the map's red channel, smoothness from its alpha times `_Smoothness`. Keyword off: the scalars, as URP Lit itself reads them. Albedo-alpha mode: the base texture's alpha times `_Smoothness`. The keyword-off capture is what separates "read the keyword" from "always read the map", and the row is also the proof that a material's keywords reach an override-shader draw. **Passed 2026-09-04**: keyword on, smoothness 102 of 102 (0.5 × 0.8) and metallic 191.5 of 191 (0.75); keyword off, 204 of 204 and 25 of 25.5; albedo alpha, 122 of 122 (0.6 × 0.8) and 191.5; albedo (154, 55, 8) in all three |
| S13 | — | **Suite.** URP's `_CameraNormalsTexture` beside `_VRSLNormalsTexture` in the same frame, at priming Forced and Disabled, each at MSAA 1 and 2 | Identical wherever URP can draw it, and the configuration where it cannot named. The row reads whether URP actually primed (the renderer's internal `useDepthPriming`, by reflection) and judges priming Forced at MSAA 2 as the failing case only where it did; where URP declined to prime, as stock URP does for a multisampled camera, that frame must match like priming Disabled. **Passed 2026-09-03** on a URP modified to prime multisampled cameras: at 2x with priming Forced, a Render Graph execution error every frame and a camera that rendered nothing (0% lit), which is why the policy refuses MSAA with priming anything but Disabled. **Passed 2026-10-11** on Basis's URP 17.7, which declines like stock URP: primed only at Forced 1x, all four configurations identical, 164409 of 164409 pixels, max 0/255; reporting "primed" at Forced 2x fails the row. On such a URP the policy's refusal costs a normals pass URP's texture would have saved, since the policy can't see the decision before the frame. **Asks for MSAA on the rig's target and holds the pipeline asset to it**: a camera rendering into a texture takes its sample count from the texture, and the host rewrites the asset mid-run |

### Occlusion

| # | Path | Scenario | Expected |
|---|---|---|---|
| O1 | — | `contactShadowStrength` 0 → 1, avatar standing in a beam | Shadow appears at its feet |
| O2 | — | Walk the occluder towards the edge of frame | Shadow fades out as it leaves screen. **Expected** — screen-space only |
| O3 | — | Wall between fixture and a surface across the room | Still lit through the wall. **Expected** — no light-perspective shadows |
| O4 | — | `Standard` against `High` on thin geometry | Less light leaks through at `High`, which traces further and samples more finely. The sample count is the level's, not a field |

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

`VRSLQualityPresetTests` in the suite covers Q3 to Q5. Q1 and Q2 want a frame capture,
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
| W1 | — | Every GUID in the wiring table, loaded and its path checked | All seven resolve, and every one to a path inside this package. Seven rather than eleven because the two managers share four assets between them and the table is walked by asset identity. A GUID that has drifted onto the legacy package's identically-named copy loads perfectly well and is the exact fault the table exists to prevent. Reported together rather than one at a time: a GUID regeneration moves every asset at once, and a row that stops at the first reads as one broken field rather than as a table that no longer describes anything |
| W2 | — | Every field name in the table, against the manager it names | Present on the manager. The table addresses fields by name, so a rename compiles cleanly everywhere else and stops resolution dead without saying so |
| W3 | — | Resolve a manager with one field deliberately pointed at the wrong asset and the rest empty | The override is untouched and is not reported as filled; every mandatory field is filled exactly once; nothing is unresolvable. Then `includeOptional` fills the remaining three, which is what the repair button does, and a second pass changes nothing — a repair pressed twice must not report changes it did not make |
| W4 | — | Two nested holds on auto-resolution, the inner one released first | The outer hold survives. Held as a set, not a count: the inner scope releasing the outer one's hold would let the next bounce refill a field the outer holder emptied on purpose, which is how quality `Off` comes back on while its counters still say it is gone. Disposing the same hold twice must not drop somebody else's |
| W5 | — | Two managers, the owner switched off so ownership passes to an unwired standby | The standby's mandatory fields are filled by the time it sets itself up. **The handover path never goes through `OnEnable`**: an owner standing down calls the successor's `TakeOwnership` directly, so a resolver hooked only there leaves this manager building its materials and passes from empty fields |
| W6 | — | Empty every field through `SerializedObject`, then run the validate pass | Still empty immediately afterwards, filled on the next editor tick. Resolution is deferred on purpose, because `OnValidate` is also where import and mid-edit serialisation live. The tick is driven rather than waited for: `EditorApplication.delayCall` does not fire inside a batch-mode play-mode test, so waiting on it would test Unity's scheduler and nothing of ours |
| W7 | — | An override on one field, the rest empty, through the enable path and then the deferred validate path | The override survives both, and each path fills what was empty. **Asserted after each path rather than once at the end**: a single assertion cannot tell which path did the work, so removing the enable hook would leave the validate pass filling everything and the row still passing while the path it names went untested |
| W8 | — | A decoy asset with the same name as a wiring target, elsewhere in the project | Resolution picks this package's copy, by asset identity rather than by name. The row checks the decoy genuinely collides before it judges anything: if a name search is not ambiguous, resolution had nothing to get wrong and passing means nothing. It stages the collision rather than gating on `com.acchosen.vr-stage-lighting` being installed, because a gated row in a project without that package compiles out and proves nothing |
| W9 | — | Every consequence string in the table, once per distinct asset | Names what the author will see, not what is null. A consequence that repeats its own field name, or that talks about being null, is the kind of message the table exists to replace, and is easy to write by accident when adding a field |

**W10 to W12 are hand rows.** W10, W11 and W11b are judged by reading the inspector, not by
the message existing: running them on 2026-08-31 found three faults no reading of the
source would have shown, a warning icon that belongs to Unity's dialog rather than to us, a
`(s)` pluralisation that reads fine in a source file and wrong beside a real number, and a
paragraph naming a feature half its readers do not have. W12 is a player comparison, and
the suite runs in the editor.

**Select the shipped prefab, and clear fields on the prefab asset rather than on a scene
instance.** Auto-resolution skips persistent objects, so a field cleared on an instance is
filled straight back in on the next editor tick and the warning branch never appears long
enough to read. That is auto-resolution working, not a fault, but it makes the warning look
unreachable. A bare `AddComponent` is a different state again: the mandatory ones fill
themselves and the optional ones do not, so it lands in W10's second half rather than its
first.

| # | Path | Scenario | Expected |
|---|---|---|---|
| W10 | — | Select the shipped manager prefab asset and look at its inspector, then clear one of the optional fields | One line reading `Set up and ready.` and a fold closed by default, labelled `Assets this uses, and which ones fill in automatically`; opening it reaches every field, so an author who wants to point one somewhere else on purpose still can. Clearing an optional field must give a **plain grey sentence** naming what is currently switched off, and a **Turn these on** button — no warning box, no icon. An empty optional field is a choice, and a warning that never goes away is one nobody reads by the third time. **Passed 2026-08-31**, and only by being looked at: the fold's paragraph named `no tile culling, no surface prepass, no beams, no strobe timing` at both managers, but one `DrawFold` serves a DMX manager with ten fields and an AudioLink one with five and no strobe timing at all, so half its readers were told about a feature they do not have. It quotes no count and names no field now |
| W11 | — | A mandatory field cleared, then a field whose asset is genuinely gone from the project | The first: a warning naming what you will see rather than what is missing (`Nothing lights from the video at all (gridTextures)`), and a **Set these up for me** button that works. A list, never a count: two missing settings go wrong in two unrelated ways, and "2 settings are missing" tells nobody which of their problems this is. The second cannot be repaired and must say so in the same register, ending `Looked for an asset that is not in this project any more`. **Passed 2026-08-31.** Stage the second by moving an asset and its `.meta` out of the package — `VRSLDMXLightUpdate.compute` is the one to use, since its only referrers are the three manager prefabs, where moving a decode texture leaves missing references through the CRT chain and every material on it. Moving both files keeps the GUID, so putting them back restores it exactly |
| W11b | — | Press the repair button on a manager whose wiring **can** be filled | **No modal dialog.** `EditorUtility.DisplayDialog` draws a yellow warning icon whatever the message says and no API changes it, so reporting a clean repair through one puts a warning on a deliberate act that worked — which is the choice-against-fault distinction the panel above it spends its whole wording making. A clean repair logs to the Console and lets the panel flipping to `Set up and ready.` be the acknowledgement, under the cursor that just pressed the button. The dialog is kept for a repair that could not finish, where the icon is right, and for nothing being selected, where no panel changes. **Passed 2026-08-31**: the field fills, no dialog opens, and the Console line reads as information rather than as a fault. Found in the first place by pressing the button; neither this nor the `1 setting(s)` pluralisation beside it is visible in the source, which is why W10 and W11 are judged by reading a rendered inspector rather than by review |
| W12 | — | The player sweep before and after the wiring change, compared against a floor measured on the same machine | **Every row unchanged.** Measured 2026-08-31, RTX 3090, IL2CPP player, 30 configurations: 30 unchanged, none regressed, largest movement -0.595 ms against a 1.086 ms floor. **Read it as a confirmation, not as the evidence.** A floor that size calls almost anything unchanged, and the structural argument is stronger: every line the wiring adds to the runtime assembly is inside `#if UNITY_EDITOR`, so none of it exists in a player, and the only change that does ship is a prefab's strobe-timer reference in a scene that drives DMX from a channel source and never samples the CRT chain. Derive the floor on the machine that judges the comparison; `Compare` refuses a reference from another GPU |
| W13 | — | Each of the fifteen slots in the shipped Grid Textures asset, against the GUID of the texture that layout ships | Every slot holds its own layout's texture, the Vertical and Legacy strobe timers included. Those two are the reason for the row: the 0.2.0-rc.6 Vertical and Legacy managers both pointed at the Horizontal strobe timer, so their strobes took their phase from a grid nothing wrote to, and the same slip inside the asset would look just as healthy |

### Grid layouts

`VRSLGridLayoutTests`, `VRSLGridDecodeTests` and `VRSLGridSwitchTests` in the suite. The DMX manager reads one of three grid layouts,
Horizontal, Vertical or Legacy, from its DMX Input setting (`dmxInput`), and takes that layout's
decode textures from a Grid Textures asset. A manager saved with 0.2.0-rc.6 named its five
decode textures directly instead; its main texture survives in a hidden field, and the
upgrade reads the layout off it. Without the upgrade a Vertical or Legacy world comes up on
the Horizontal grid and lights the wrong fixtures with nothing in the Console. It runs on
the enable path, the deferred validate path and a scene build step, which Unity also calls
for scenes built into AssetBundles (checked 2026-10-09 on 6000.7.0b4 against
`BuildPipeline.BuildAssetBundles(BuildAssetBundlesParameters)`, the call Basis builds worlds
with).

| # | Path | Scenario | Expected |
|---|---|---|---|
| G1 | — | A switched-off manager staged with an rc.6 main texture through the hidden field, once per shipped layout, brought across by each of the enable path, the deferred validate path and the scene build step | Comes up in the layout its texture belonged to, with the hidden field cleared and its live main texture that layout's from the shipped asset. Nine cases, because a path that only handled Horizontal would pass the Horizontal ones by doing nothing |
| G2 | — | The same with a texture that is none of the package's, on each path; then the manager given a Grid Textures asset of its own | Stays Horizontal, keeps the old texture and warns that it needs a Grid Textures asset made from its textures. Once it has an asset that is not the package's, the next upgrade clears the field and says nothing more. Guessing a layout from an author's own texture would be wrong as often as right, and dropping the reference would lose what the author needs to build the asset |
| G3 | — | **Hand row.** Select a DMX manager, then one carrying an rc.6 main texture of the author's own | **DMX Input** is the first control in the cost group, directly above Quality, offering Horizontal grid, Vertical grid, Legacy grid and Stream data, and its tooltip says what each reads, that the wrong grid lights the wrong fixtures, that Stream data needs a source and is dark without one, that a grid takes a few frames to settle, and that `SetDmxInput` takes the same choices as numbers. Under it: a source in the scene on standby under a grid; under Stream data the source it reads, or a warning that there is none and the lights will be dark. The **Asset references** fold shows Grid Textures and a line naming the layout it is read for, or that it isn't read under Stream data. The rc.6 manager also shows a warning naming its old texture and how to make a Grid Textures asset from it; the warning goes once one of the author's own is assigned, and the old reference is cleared. The AudioLink manager's inspector is unchanged. **Passed 2026-10-10**, from Inspector captures in a Basis project: the shipped Horizontal manager with no source, then a synthetic source added ("Synthetic DMX Source is on standby. Choose Stream data to read it."), Stream data ("Reads Synthetic DMX Source."), the source switched off (the no-source warning), the fold on Stream data ("Grid Textures is not read under Stream data.") and on Legacy ("… read for the Legacy grid, which DMX Input sets."), a manager staged with an rc.6 main texture of its own and then given a Grid Textures asset of its own, and the AudioLink manager without the control |
| G4 | — | All fifteen of the shipped Grid Textures' CRTs set to Realtime, then a manager brought up at each layout | Only that layout's five are Realtime, the other ten are OnDemand, and `_VRSLU_DMXGridLayout` holds the layout. Staged Realtime first because the package ships every CRT OnDemand, so in a clean project nothing runs that the manager did not start and the row would pass by doing nothing. The stop is for assets an earlier session or another tool left running, which would otherwise decode a grid nobody reads, every frame. **Switch half**: staged the same way, a manager on Horizontal switched to Vertical by `SetDmxInput` and then to Legacy by writing `dmxInput` (what the inspector or an Undo does in Play mode) runs only the new layout's five each time, and the global follows. A field written and the call made in the same frame binds at once rather than waiting for `LateUpdate`; a field written and then the call asking for the layout already bound puts the field back, and nothing switches. Once the manager lets go all fifteen are back at Realtime as staged |
| G5 | — | The fifteen CRTs staged at a mix of Realtime, OnDemand and OnLoad; a Vertical manager owns, hands over to a Legacy one, which is then switched off | Every CRT is back at its staged mode, the layout global read Vertical, then Legacy, then 0 (Horizontal) once nobody owns the singleton. **The order is the claim**: a manager puts its modes back before it hands over, so its successor notes the assets as they really are. Restoring after the hand-over fails the row, naming four CRTs left as the first manager had them. In the editor a mode set on a CRT asset is written to the asset, which is why Play mode has to leave them as it found them |
| G6 | — | The rig's ramp through the channel buffer, then the same values on a painted 50 by 50 Legacy grid with the source detached: every channel the rig patches read back through the compute, and the fixtures' light | The compute reads every channel from the texel the shader-driven fixtures' include reads it from. Those texels are found on the GPU (`VRSLDMXRig.CellsOf`: the include's own cell position, sampled against a texture whose texels hold their coordinates), not re-derived, so a position on a texel edge lands where the sampler puts it. Every channel with a texel of its own decodes what the buffer holds, and every fixture lights the same colour and intensity as from the buffer. **One known exception, outside the Legacy port.** On this GPU 38 channels share a texel in pairs, a sector's 13th and the next sector's 1st: a Legacy row holds two sectors, and on the 50 by 50 textures their positions (u 0.50 and 0.52) are a texel apart on texel edges, which settle onto one texel. Another GPU may settle them differently, so the row names no shared set and judges those channels only against what the fixtures read |
| G7 | — | The rig brought up again with no Grid Textures and resolution held off, so nothing fills them, a channel source publishing | Every fixture inside the published universe lights the colour its channels say. The buffer path reads no grid, and the decode binds a black stand-in for each grid texture the layout lacks rather than skipping the dispatch, since an unbound compute texture is an error even on a branch not taken. Passed 2026-10-11 |
| G8 | — | The rig's movers and one of each Horizontal fixture prefab (flasher, laser, light bar, 15-channel bar, 6x4 strobe, Built-in discoball, and the four realtime prefabs' bodies), lit from the same channel values painted on a Horizontal, a Legacy and a Vertical grid at each layout's decode-texture size, the manager set to the matching layout, captured from in front of the prefabs | The Legacy and Vertical frames match the Horizontal one, both with the layout set before the manager came up and with the manager brought up on Horizontal and switched by `SetDmxInput` (the switch half): 0 px in all four, measured 2026-10-10. Shader-driven bodies and beams read through both includes, the realtime bodies and the realtime light all follow the manager's layout, so one prefab serves every layout. The Vertical case is the Built-in include's, which takes its texel size from Unity's automatic value for the grid texture rather than from the manager. Channels that share a texel in any layout carry one value between them, so the frames compare the read and not the layout's capacity (G6). Leaving the manager on Horizontal with a Legacy grid moves 10.6% of the frame, which is the row's sensitivity. **The Built-in discoball turns with the shader clock**, which no capture holds: two captures of one scene differed by ~600 px until the image rows set its `_RotationSpeed` to 0 on their own material copies |
| G9 | — | Every `.shader`, `.hlsl`, `.cginc` and `.compute` under `Runtime/Shaders`, read as text | `_EnableCompatibilityMode` appears only in the two CRT shaders (each layout has its own CRT set, so for them the material is the layout), `_EnableVerticalMode` nowhere, both read-function includes choose Legacy by `_VRSLU_DMXGridLayout == 2`, every shader including the Built-in read functions has the global declared (the URP include brings its own Defines file), and `VRSLDMX.cginc` is gone. Materials still carrying the old flag's saved value are not checked: nothing reads the name. Passed 2026-10-11 |
| G10 | — | `VRSLGridDecodeTests.G10_the_legacy_and_vertical_prefabs_render_as_before`: the Legacy and the Vertical fixture prefabs 0.2.0-rc.6 shipped, beside the rig's manager set to their layout, lit from painted grids as in G8, strobe held | The frame matches this machine's previous capture at I1's budget, like I1: the first run seeds it, and after that the row guards against change, with every fixture taking the layout from the manager. **Seeded 2026-10-11**, after the par and blinder bodies came to follow DMX (J5 judged that look first), and verified at 2 px and 0 px in a filtered run and at 1 px and 27 px (0.010%) in a full-suite run. Delete `rc6-<layout>-prefabs-v2.png` from the host's `VRSL-Benchmarks/images` to re-seed after an intended change |
| G11 | — | The rig on the shipped Grid Textures with no channel source, each layout's video grid painted with a pattern of its own (`VRSLDMXRig.PaintVideoGrid`: every channel over the block of video texels behind the decode texel the fixtures read it from), switched Horizontal, Vertical, Legacy and back to Horizontal by `SetDmxInput`; after each switch strobe held while the colour settles, then 60 frames with strobe running, every fifth fixture's strobe channel high and the rest at zero | After each switch every fixture lights the new layout's pattern through that layout's own decode chain (5 frames to settle on Horizontal and Vertical, 10 on Legacy, 2026-10-10), the layout global and the live main texture follow, and the fixtures re-collected after the switch are the ones already held, so the light data keeps its order. On Horizontal and Vertical every strobing fixture changes state and every held one stays on. **On Legacy the row only judges that something strobes**: its strobe timing and output textures are 25 by 25 against a 50 by 50 grid, as upstream ships them, so one strobe texel serves four cells and which fixtures strobe depends on the neighbour it samples (3 of 10 measured). A timing texture left over from another layout is stopped and holds every fixture still, which is what the strobe half catches. **In batch mode nothing gives the decode textures a shader clock between frames**, so they saw `_Time.y` under a second however long the row ran and the strobe timing never left its start-up branch; the row sets `_Time` and `unity_DeltaTime` itself from a `LateUpdate`. And Render Graph sets every global texture it published back to black once a frame has rendered, so the strobe output, which updates between frames, read the main grid as black and held every fixture open: the manager publishes the main grid for it under `_VRSLU_DMXGridStrobeStatus` as well, a name the graph never sets. With the shader pointed back at the graph's name, every strobing fixture holds on and the row fails |
| G12 | — | A manager on Grid Textures holding only a Horizontal set asked for Vertical by `SetDmxInput`, for input 7, and given Legacy by writing the field; then a second, enabled manager that does not own the singleton asked for Legacy, and the owner switched off | Each refused switch logs one warning saying why, in the author's words, and changes nothing: the setting stays Horizontal (the field is put back) and the layout global stays 0. The non-owner takes the setting and touches neither the global nor any texture's update mode; handed the singleton, it binds Legacy and publishes it (C6 to C8's rule for a non-owner). Passed 2026-10-11 |
| G13 | — | The control panel on the rig, its light slider assigned, the manager switched to Vertical with Vertical's video grid painted as in G11; the slider halved, then the panel's strobe toggle pressed | Held fixtures cast half the light after the switch. The Vertical strobe output closes somewhere while strobe runs (the row's sensitivity), and with the toggle on it reads open everywhere for 30 frames: the toggle sets the manager's `disableStrobe`, which the manager publishes as `_VRSLU_StrobeDisabled` and the strobe output decode reads, so it reaches every layout without writing to a material. Every float, int and keyword on the fifteen decode materials is unchanged. Their dirty counts are no use as the check: the decode chain's own updates raise them. Passed 2026-10-11 |
| G14 | — | The standard 30-row player sweep, 0.2.0-rc.6 (`0e64c0f`) against the package with grid layouts on one manager, on one machine, editor and host project; each player captured more than once, interleaved, so each row's run-to-run spread sits beside its A/B. And G11's log of a switch's cost | **Measured 2026-10-10** (RTX 3080, Unity 6000.7.0b4, a Basis project, IL2CPP, MSAA 2x, priming Forced): across the 30 rows the median change in the package's GPU cost is 1.3% against a median run-to-run spread of 4.2%, and no row is slower by more than its own spread, so a manager that never switches costs what it did. Emitting fixtures match row for row. A stored reference from another editor can't judge this, since `Compare` refuses across editors, which is why the row is an A/B. A busy box swings the outside-the-cones rows by up to a quarter between two captures of one player, so read a single pair as a reading, not a result. **A switch**: the bind costs 0.02 to 0.03 ms after a run's first (0.6 to 0.8 ms for the first, first-use costs included) and the refresh that follows is a fixture search, 0.12 to 0.17 ms for 50 fixtures, when the fixtures are unchanged; batch-mode CPU time, so an order of magnitude rather than a frame-time figure. The incoming decode chain settles in 5 frames (Horizontal, Vertical) and 10 (Legacy), G11 |
| G15 | — | **Hand row.** Play mode, the shipped Legacy manager prefab and one realtime fixture, no channel source, the Legacy video grid painted with a value per cell, then **DMX Monitor** | `Reading` shows `Video grid — Legacy`, naming any source on standby (`Video grid — Legacy (VRSL_SyntheticDMXChannelSource on standby)`), the Grid line names the 50 by 50 Legacy decode texture and one universe, the universe selector reads 1 of 1, and the only note under it is the nine-universe one: nothing says Legacy cannot be read. Every channel shows the painted values and the padding past 512 reads dark. **Passed 2026-10-10** from a capture of the window; leaving Play mode put every decode texture's update mode back. Not re-run since every reader came to take a channel from its own cell (G17), which removes a difference in the 13th column that run showed |
| G16 | — | G8's ten Horizontal prefabs and the rig's movers, captured once patched at sectors 2 to 11 and once at sectors 106 to 115 (channels up to 1524, the end of the Horizontal grid's third universe), the same values painted at both | The two frames match at I1's budget: 1 px, 2026-10-10. The laser, lens flare, discoball and mover bodies read through the Built-in include, which sizes the grid from Unity's automatic texel size for the grid texture rather than from the manager's `_VRSLDMXTexelSize`, and a wrong size moves the read further from its cell the further down the grid a fixture sits. Stretching that include's row coordinate by 1% moves 2.1% of the frame. Channels past 1536 share texels with the first three universes (the grid's nine-universe packing), so the row stops short of them |
| G17 | — | `VRSLGridDecodeTests.G17`, once per layout: every channel the layout carries (Horizontal 1536, Vertical 871, Legacy 650) painted in its own cell with a value its neighbours don't share, the cells derived from channels no reader can place wrongly (`VRSLDMXRig.OwnCellsOf`: slot 1 of each sector, and the slots of a reference sector), then read back through the compute and through both includes' value, movement and raw (spin) reads | Every reader reads every channel from its own cell, a sector's 13th included; channels sharing a Legacy texel are left out. Passed 2026-10-11. Before the change the compute and both raw reads misread exactly the multiples of 13 where a float32 multiply by 1/13 lands above the integer: 38 on Horizontal, 22 on Vertical, 4 judged on Legacy (91, 169, 182, 195, 338 to 403, 676 to 819, 1339 to 1534), the channels the old correction table listed |

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
| E1 | — | The rig on the shipped Grid Textures with the synthetic source attached (Fixtures, held still), each layout's video grid painted with a pattern of its own as in G11, strobe held; cycled Stream data, Horizontal, Vertical, Legacy, Stream data, Horizontal by `SetDmxInput` | Every fixture lights the chosen input's values: Stream data the frame after the call, each grid within its chain's settle time (5 frames Horizontal and Vertical, 10 Legacy). Under a grid the source stays bound and the channel count is 0; the layout global follows. The source's values match none of the grid patterns, so a fixture reading the wrong input shows. **Passed 2026-10-10**; fails with the manager publishing the source on a grid input |
| E2 | — | **E1's image half.** N29's and N30's scenes with the synthetic source and a grid of other values both present, brought up on Stream data and switched to the Horizontal grid at runtime, and the other way round | Each frame matches the same scene brought up on the input it was switched to, shader-driven bodies and realtime lights alike. The two inputs' frames differ, which is the row's sensitivity. Passed 2026-10-11 |
| E3 | — | `SetDmxInput` given 7, -1 and 4 on Stream data, and 9 on the Vertical grid; then Stream data chosen with no source | Each number that is no input logs one warning naming the numbering and the input the lights stay on, and changes nothing: the source is still read. Stream data with no source is not refused. G12 covers a grid the Grid Textures can't decode. Passed 2026-10-11 |
| E4 | — | The rig on Stream data, lit from the synthetic source, then the source component switched off | The lights are dark two frames later (every fixture's light data zero, no fallback to a grid or to the buffer's last values) and stay dark. No message in the first four seconds; one at the five-second grace, saying Stream data has nothing sending it and what to add; none after. Switching the source back on lights the rig again. **Passed 2026-10-10**; fails with the decode skipped when there is nothing to read |
| E5 | — | The rig with no source: Vertical applied twice; Stream data chosen a second before a synthetic source enables; a second manager, enabled after the owner, given Legacy twice; then the owner switched off | Twice ends as once (same live texture, update mode and layout global). Stream data chosen early reads the late source with no message. The spare takes the setting and the owner is unaffected. Handed the singleton, the spare keeps its own input (Legacy) and the source moves with the singleton, on standby: count 0, Legacy's textures live. Passed 2026-10-11 |
| E6 | — | `VRSLGridLayoutTests.E6_a_manager_not_yet_looked_at_comes_up_on_its_grid_or_its_source` and its siblings: a switched-off manager the upgrade hasn't looked at (saved with 0.2.0-rc.6, or added since: its input and no version), once per grid, with and without an enabled channel source in its scene, through the enable path, the deferred validate path and the scene build step; then a manager already looked at and one whose input a call chose, each beside a source, and an rc.6 Vertical manager beside one | Without a source each comes up on its own grid; beside one, on Stream data, since 0.2.0-rc.6 read a source whenever it was sending. Every case is marked as looked at and is never changed again: the looked-at and the call-chosen managers keep their grids beside a source. The rc.6 rule runs first (the old texture is cleared) and the source rule after it. The shipped manager prefabs carry no version, because a scene's manager inherits what its prefab says and would otherwise never be looked at. **Passed 2026-10-11**, 21 cases; the nine beside a source fail with the source rule taken out |
| E7 | — | The rig's manager on Stream data reading the synthetic source, then on the Vertical grid, then on Stream data with no source, `VRSL Diagnostics` logged in each | The summary names the input on a line of its own: `DMX input: Stream data from VRSL_SyntheticDMXChannelSource`, `the Vertical grid, VRSL_SyntheticDMXChannelSource on standby`, and `Stream data, and NOTHING IS SENDING IT` with what to do. A Truss source that has decoded no record yet is named as such. The **VRSL Diagnostics** context-menu entry is on `LogDiagnostics`, where an author reaches the report. **Passed 2026-10-10**; the menu check fails with the attribute moved onto another method |
| E8 | — | **The choice.** `VRSLBasisVideoOutputTests` (Basis integration only). The Basis video output's target and corner choice as a pure function, against the shipped Grid Textures | Following on, the live layout's raw grid (`DMXRTViewer-RAWValues-<layout>`, found as the texture its main decode texture's material samples) with that layout's corners; following off, the DMX Grid RT with the single corners; under Stream data nothing, either way; with no manager the DMX Grid RT as before; an author's main texture that is no decode texture gives no raw grid and falls back to the DMX Grid RT with the layout's corners. **Passed 2026-10-10**; fails with the Stream data pause taken out |
| E9 | — | **A real player.** `vrsl-dmx-marching.ts` playing, the output following the manager with the fixture's strip framed in all three corner sets and the Horizontal raw grid as its DMX Grid RT; the manager switched Horizontal, Vertical, Legacy, then Stream data with every raw grid blackened, then Horizontal | On each grid only that layout's raw grid receives the frame and the other two stay black (the outgoing one is cleared). Under Stream data none is written over five frames. The first frame after choosing Horizontal again writes its raw grid. **Passed 2026-10-10** with the fixtures from disk; fails with the Stream data pause taken out |
| E10 | — | The standard player sweep with a source read whenever it published and the grid chain running alongside (the package before a grid input put a source on standby), against the package after, both players built from clean checkouts, three captures each in interleaved rounds: after on Stream data against before, and after on the Horizontal grid (`-vrsl-dmx-input 0`, source on standby) against before with its sweep's source switched off, both on an unpainted grid with the fixtures dark | Stream data costs less by the decode chain it no longer runs, and a grid input costs what it did. **Run 2026-10-10** (RTX 3080, Unity 6000.7.0b4, a Basis project): Stream data cheaper, package GPU median change 5.4% against 13.3% median spread, the 7 rows outside their spread all cheaper (0.78 to 0.95 of before), none slower beyond spread. Grid: no systematic change (frame median change 4.4% against 21.3% spread, 2 rows outside, one +4.6% and one -7.7%), but dark-grid captures are bimodal from run to run, so it rules out only a large change; a lit grid would need the sweep to paint the video grid. Build each player from a clean checkout: a working tree whose decode textures were saved as Realtime keeps the chain running in the player (N35) |
| E11 | — | **Hand row.** Play mode in a Basis project: the shipped Horizontal manager, eight movers at sectors 0-7, the synthetic source (Fixtures) bound, and a recorded show whose Horizontal grid sits in the bottom 208 px of a 1080p video, framed into the Horizontal raw grid every frame by the video output's `BlitUVs` and played through a Unity VideoPlayer. A uGUI Dropdown with the four options, `On Value Changed (Int32)` wired to `SetDmxInput` by a persistent listener, set to each in turn | Every choice switches every fixture: Horizontal lights all eight from the show (intensity 10, colours following the video); Vertical and Legacy are dark, nothing writing those grids; Stream data reads the source (2080 channels, its colours); back on Horizontal the show again, 8 of 8 lit over ten samples. The DMX Monitor's header reads `Video grid — Horizontal (VRSL_SyntheticDMXChannelSource on standby)`, `Video grid — Legacy (…)`, `Stream data — VRSL_SyntheticDMXChannelSource`, and with the source unbound `Stream data — nothing is sending it, lights dark` with its explanation; the inspector shows the standby line on a grid and "Reads Synthetic DMX Source." on Stream data. **Passed 2026-10-10.** The synthetic source binds only in its own `OnEnable`, so in a scene entering Play it can miss the manager and needs assigning, as the rig and sweep do (the Truss sources rebind in `Update`) |
| E12 | — | **Basis.** Whether a Basis world bundle may call `SetDmxInput` from a UI event, and from an interpreted world script | **By reading Basis's source (2026-10-10), not run in a client: no, on stock Basis, either way.** World scenes load with content removal and persistent-listener scrubbing on (`BasisBundleLoadAsset`, `UseContentRemoval` and `ScrubPersistentUnityEvents`). `SceneContentPoliceSelector.asset` lists the Dropdowns (uGUI and TMP) and the Basis media player but **no VRSL type**, so the manager itself would be removed from a loaded world and a listener aimed at it switched off (its target type is not approved). The Cilbox allow-list has no VRSL type either. Both need Basis to approve `VRSL.URP.VRSL_URPLightManager` (and, for the lights to exist at all, the package's components); worth confirming against the Basis a world actually ships on |
| E13 | — | **E5's two-client half, hand row.** Two Basis clients in one world with the synced input recipe from the wiki; one changes the input, then a third joins | Both clients follow the change, and the late joiner comes up on the current input with no message about Stream data. **Not run**: needs two Basis clients on a networked world, and E12 says a world can't reach the manager on stock Basis yet |
| E14 | — | **Hand row, the real Basis path.** A Basis project booted from its initialisation scene in Play mode; a rig added at runtime: the shipped Horizontal manager, eight movers at sectors 0-7, a `BasisMediaPlayer` with the SEI record source (`BasisUserDataToVRSLDMX`, three universes) and the video output following the layout with the marching strip's corners, and the Dropdown wired to `SetDmxInput`. `LoadUrl` on `vrsl-dmx-marching.ts`, the URL prompt accepted by hand; then the dropdown switched Horizontal grid, Stream data, three times each way while it played. Every fixture's ramp offset recovered from its decoded red channel (the fixture puts `(c - 1 + 5 * frame) % 251` on channel c) | Under the grid input the source is on standby (0 channels published) and keeps decoding (records counting on continuously across every switch, none dropped); under Stream data it publishes 1560 channels. All eight fixtures agree on one offset each time, and it names the frame: the grid within a frame of the one presented (frame 176 expects 127, read 126-127; 221 expects 101, read 100-101), the records exactly from the first read after each switch (199 expects 242, read 242; 244 expects 216, read 216). At the end of the video both inputs read frame 899 (offset 228). **Passed 2026-10-10.** The player has no display in this rig, so nothing shows on screen; the frame is only what reaches the grid and the records |

### Migration

`VRSLMigrationTableTests`, `VRSLMaterialReferenceTests` and `VRSLScriptReferenceTests` in
the suite, and three hand rows. The migration tool (**VRSL → URP → Migrate Scene Fixtures**) compiles only in a
project with `com.acchosen.vr-stage-lighting` installed, which none of the test projects
has, so its table lives in the runtime assembly where the suite can read it. A fixture
prefab works on any grid layout, so Legacy and Vertical upstream fixtures migrate onto the
Horizontal URP prefabs. That they then light correctly on a Legacy or Vertical grid is G8,
which names the flasher, laser, light bar, 15-channel bar and 6x4 strobe.

| # | Path | Scenario | Expected |
|---|---|---|---|
| L1 | — | The migration table against this package | Every upstream GUID appears once across the pairs and the unported list; every URP target, and every 0.2.0-rc.6 target a row remembers, is a prefab inside this package; no pair produces a prefab from a `Legacy Mode` or `Vertical Mode` folder except the Vertical example surface, which has no Horizontal twin. Passed 2026-10-10, and failed as it should with a Legacy row pointed back at its rc.6 `-L-` prefab and an upstream GUID listed twice |
| L2 | — | Every material under `Runtime/Materials/Lights`, its shader and texture references read from the asset file | Each resolves inside this package or a package it depends on (AudioLink's `rt_AudioLink` is the one that does), and loads as a shader or a texture as its slot expects. Slots the shader doesn't declare are skipped: they're never sampled and many carry upstream references. Read from the file because a `Material` can't tell a missing reference from an empty one. Passed 2026-10-10; on 0.2.0-rc.6's materials it names the Legacy and Vertical flasher masks and the Legacy 5-channel par's shader, and nothing else |
| L3 | — | A Vertical flasher prefab (`-V-`) on a Vertical-layout manager, flashing | Glows at the lens, not over the whole body. A hand row because the change is what the fixture looks like. Not yet run |
| L4 | — | A throwaway project with AcChosen's VRSL v2.3.0 (it carries both Legacy sets) and this package, a scene holding one instance of every Legacy fixture prefab from both sets and a VRSL-URP manager set to Legacy, every serialized value on each legacy fixture component moved off its default; run the migration twice and compare each sibling with its source, then light a known pattern | The summary pairs every mapped fixture, counts them by name, and names only the example surfaces and the 5-channel Vertical light bar as left behind; each URP sibling sits right after its source, in its pose, and carries every value the tool copies, the AudioLink band included (an int in v2.3.0, an enum here), with tints scaled by the two prefabs' defaults; it answers its own channels. The second run adds nothing. **Migration half passed 2026-10-11** on 6000.7: 52 of 52 siblings, 1,018 of 1,018 carried values, a second run skipped all 52; before the band fix, the band was the only value lost (8 of 1,026). Lighting a known pattern not yet run |
| L5 | — | L4 against AcChosen's current `main` (the first Legacy set only), plus one Legacy fixture with an rc.6 `-L-` sibling already beside it | As L4, and the fixture already migrated with rc.6 gets no second sibling. **Migration half passed 2026-10-11** on 6000.7 with VRSL 2.8.4's three `GetInstanceID()` lines patched (it doesn't compile from Unity 6000.5 as shipped): 44 of 44 siblings, 967 of 967 carried values, the rc.6 fixture left alone, a second run skipped all 44. Lighting a known pattern not yet run |
| L6 | — | Every prefab and scene in the package, read from the file | Each script a component names loads from this package or a package it depends on (dependencies of dependencies count: URP's `Volume` is in the core package). A VRChat script, or one whose whole body compiles only under UdonSharp, would arrive in every scene as a missing script. Passed 2026-10-11, and failed as it should on the 0.3.0-rc.1 prefabs, naming VRChat's `UdonBehaviour` on 18 prefabs and its UI shape and AudioLink's `GlobalSlider` on the smoothing controller |

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
| H1 | — | **The null run.** Capture, change nothing, capture again, compare | Every row **unchanged**, and the stated noise floor at or above the spread the two runs actually showed. Judge the guards as much as the verdict: each capture must report the package costing something. A run of zeroes compares as unchanged against anything, so a row of all-zeroes passing is the failure this row exists to catch, not a pass. Measured 2026-08-24 headless: package cost 0.257 and 0.293 ms CPU, delta 0.037 ms against a floor of 0.059 ms |
| H2 | — | **The verdict.** Two synthetic runs alike but for one row's package cost | A move past the floor reads as regressed, an equal move the other way as improved, and anything inside the floor as unchanged, including a delta exactly equal to it, because a `<=` is a decision somebody made. Judged on the comparison rather than on a seeded build, so it runs on any clock. **The seeded form was retired**: neither lever it named could clear the floor in that scene (clearing the cull cost 0.0015 ms while the counters showed it rejecting nine tenths of the per-light work, and quality `High` cost 0.089 ms against a 0.097 ms floor), so it ran on an absurd step count, and no such field exists now. H14 is the row that still takes a real workload change end to end |
| H3 | — | Three captures of an unchanged scene; measure the spread across all three | The spread must be within a tolerance **declared in code**: 40% of the measured cost, with a 0.05 ms floor for costs too small for a fraction to mean anything. Judged against a declared figure rather than one derived from the same three runs, because a floor taken from the data it adjudicates cannot fail. It is a smoke test for a harness that has stopped working, not a precision certificate: batch mode reproduces poorly and inconsistently, measured at 5.9% of the cost on one run and 33.9% on the next at 48 frames a side. Raising to 160 frames a side brought it to around 10%. Measured 2026-08-24: 0.208, 0.190 and 0.188 ms CPU — a spread of 0.021 ms, which is 10.4% against the 40% allowed. It does get close: another run the same day spread 36.7%, so the tolerance is doing real work rather than sitting far above anything observed |
| H4 | — | Any capture, first one in the process | Must be **discarded**. A session's opening capture runs pinned at exactly the capture delta (both halves at 16.669 ms with an IQR of 0.02, against a 16.667 ms capture delta), so the difference cancels to zero while the counters look perfectly healthy. Idle frames do not clear it and neither does a longer warm-up inside the capture; disposing and rebuilding the rig does, which is why the warm-up is a whole capture taken and thrown away |
| H5 | — | Quality `Standard` against quality `Off` | Two halves again. The **counter** half must pass anywhere: steps per light reads 24 at `Standard` and **0** at `Off`, which is the observable that says the volumetric pass is not being enqueued rather than merely being configured down. Clearing `volumetricShader` alone does not do it (the manager builds its material once and keeps it), so a preset that only cleared the field would report volumetrics off while running them at full cost. The **timing** half needs a GPU clock, direction included. It was briefly asserted on either clock, on the strength of one favourable reading of 0.069 ms against a stated 0.021, then measured `Off` at 0.207 ms against `Standard` at 0.171, dearer by 0.036, with the counters still saying `Off` enqueued no pass at all. H3 explains it: the run-to-run spread on the CPU clock is around 0.055 ms, wider than the gap, so the sign flips as often as not. **A slim gap plus one confirming observation is not a row** |
| H6 | — | `VRSLBenchmarkScene.SetActiveFixtures` at each count in the matrix | The manager must collect exactly the count the row claims. The sweep varies fixture count by activating a subset of one truss rather than rebuilding, and the subset is evenly spaced so the spread is identical at every count — taking the first N would cluster them at one end and change lights per tile, which is a counter the sweep reports. Rounding collisions at small counts are how this goes wrong, and it goes wrong silently. Measured 2026-08-24: exact at 10, 25, 50, 100 and 200 |
| H7 | — | `VRSLBaseline.ReferencePath` with `VRSL_PERF_HOME` unset, set to a folder holding no `baseline.json`, and set to one that does | Null, null, then the path. The middle case is the one worth having: a consuming project may set the variable for the reference frames alone, and returning the path regardless turns that into "no such run" further down, which sends the reader looking for a file rather than for a baseline they never had. The rows restore the variable in teardown, not at the end of the row — it is process-wide and the image rows read it too, so a row that failed part way through would otherwise leave I2 pointed at a temporary folder for the rest of the session |
| H15 | — | `VRSLBaseline.ReferenceFor` with a machine that has its own file under `baselines/`, and with one that does not | The machine's own `<gpu>-<context>.json` where there is one, `baseline.json` where there is not. **Keyed by GPU and context both**, for the same reason the noise floor is: an editor run and a player run measure two different things and neither describes the other. Falsified 2026-08-31 by moving the per-machine file aside — the same comparison then falls back and is refused on the GPU, and matches again when it is put back |
| H15b | — | `VRSLBaseline.ReferenceFileName` with a context naming a traversal — `../outside`, `..\outside`, `/etc/passwd`, `..`, `a/../../outside` | One path segment, no separator and no `..`, and the lookup never reaches a file outside `baselines/`. **The context is input, not a constant**: it is read from the candidate `run.json` the caller was handed, so a hostile one walked out of the folder and the answer was a comparison against a file nobody chose rather than an error. Both halves of the name are reduced to letters and digits now — the GPU half always was, and applying it to only one of two inputs is what left the hole. **Reduced rather than allow-listed on purpose**, so a third context works the day somebody adds one instead of reading the editor's numbers without saying so. Falsified 2026-08-31 by passing the context through as before: this row goes red and the other six stay green |
| H16 | — | A run compared against a reference captured before `captureWidth` / `captureHeight` existed | **Compared, not refused.** Zero means the size was never recorded, which is not the same as a size that disagrees — the guard `scriptingBackend` already had, and for the same stated reason. Measured 2026-08-31 with the guard removed: `REFUSED — render size: 1920x1080 vs 0x0`, which is every run on every machine, including the reference against the machine that produced it. With it, 30 rows compare |
| H17 | — | A **player** run compared with no `-baseline`, from inside the editor | The reference is chosen from the **candidate's** environment, not from the process doing the comparing. Otherwise a player run judged from the editor resolves the editor lineage, falls back, and is refused for a difference nobody introduced — which is what it did until 2026-08-31 |
| H8 | — | `VRSLQualityPreset.Session` against an AudioLink-only scene, a scene with both managers, and no manager at all | Levels apply to whichever managers are present, and Restore puts the author's own values back. **The both-managers case is the one with teeth**: quality held on the DMX manager alone leaves the AudioLink volumetrics running at every level, so the `Off` capture is not off and the split between beams and surface lighting gets measured against a baseline that still has beams in it. Strobe is held on only where the manager has that switch — the AudioLink path strobes off the audio and has none, so it is a no-op there rather than something faked. The rows build their managers on an **inactive** GameObject, so `Awake` and `OnEnable` never fire and asking what a preset writes cannot start a light manager as a side effect |
| H8b | — | `VRSLBenchmark.MeasureDmxPath` across one path, both paths, and both with the DMX one empty | The measured path is never a manager holding no fixtures while another holds some. A single empty manager still wins, because there is no other path to name. The case worth the row is both present with DMX empty: selecting it writes a measured-path count of **zero**, and zero is what a run too old to carry the field deserialises to — so the scene reads as single-path, the AudioLink coverage gets divided by the AudioLink count, and nothing says the figures came off a path with nothing on it. Unrecoverable downstream, which is why the guard is at the selection: once the zero is written, the two states are the same data. Falsified 2026-08-27 by restoring the pre-fix rule: this row goes red and the other five keep passing |
| H9 | — | `VRSLCounters.MeasuredFixtures` and `MixedPaths` on a single-path scene, a dual-path one, and a run recorded before the field existed | The coverage figure divides by the path it was measured on, not by the scene. They agree wherever a scene has one light path, which is nearly all of them. **In a dual-path scene they must not**: the average comes off one cull pass, so read against the scene total it reports less coverage than was measured and the worst-case branch keyed on that comparison can never fire — on precisely the scenes with most to skip. The summary says which path it is describing, because run notes reach that summary only on an unusable run and would otherwise be invisible on every run worth reading. The third case is the one that goes unnoticed: an older run deserialises the field to 0, and reading it raw gives every such run a denominator of nothing |
| H10 | — | **The player run.** Build the sweep player and run it, which captures the matrix in a standalone player | 30 rows, **every one of them on a GPU clock**, with the environment block reading `Context: Player` and naming the scripting backend. Fixture counts exact at 10, 25, 50, 100 and 200, and **Emitting** non-zero on all 30. Measured 2026-08-27, RTX 3080, IL2CPP, 1920x1080: package cost from 0.354 ms (50 fixtures, outside the cones, volumetrics off) to 3.340 ms (100 fixtures, outside, High), settled after 303 frames, whole matrix in 26 s. Emitting reads 97 of 100 and 195 of 200 — that is the Ramp pattern putting a handful of fixtures on colour channels under the compute's `6/255` emission gate, not fixtures being dropped, and the tell is that it is the same handful every run |
| H11 | — | A player run compared against the reference run, which is an editor one | **`REFUSED`, a zero-row verdict line and exit 2.** Measured 2026-08-27: refused naming `depth priming: Auto vs Forced`, because `Matches` reports the first difference it finds and priming is checked before context — both differences are real and only one is named. This is the guard that keeps an editor figure and a player figure out of one table, and it is the reason the two are separate lineages with separate noise floors rather than a limitation to work around |
| H12 | — | `git status` on the host project before and after building the sweep player, **including a build killed part-way through** | No new differences, and no `Assets/VRSL-BenchBuild`. A build step that changes Player Settings and puts them back in a `finally` leaves them changed when the build is killed, since the `finally` never runs; this one writes none, and discards its scratch scene on the way in as well as out. **One thing it cannot help**: `GraphicsSettings.asset` gains an always-included shader entry, which Unity does on any batch standalone build of that project rather than the step doing it, and which has to be put back by hand |
| H13 | — | `VRSLBenchCommandLine.AdoptFloorFromCommandLine` with `-baseline` and `-candidate` naming a **player** null pair, then check the editor's stored floor | Filed under `VRSL.Perf.Floor.Player.<gpu>`, leaving the editor's bare-GPU key untouched. Measured 2026-08-27: **0.426 ms** for the player context on an RTX 3080. The row earns its place from what happened without it — that same null pair, two captures with nothing changed between them, reported **six regressions and seven improvements**. Refuses a forced comparison and refuses runs captured on another GPU, for the same reason the button in the window does: a floor is raised and never lowered, so a wrong one survives until somebody resets it |
| H14 | — | **The stopwatch.** A full before-and-after: capture, change the package, capture, compare | Under fifteen minutes of wall clock and under two minutes of attention. Measured 2026-08-27 with a wrapper script around the player build, the player and the comparison, with the lever a doubling of the Standard preset's volumetric step count from 24 to 48: capture 30 s, rebuild 228 s, capture 30 s, compare 23 s — about five minutes and three commands. Seven rows regressed, none improved, and **six of the seven were Standard rows**, which is the specificity check nobody asked for: a floor set too low would have fired on the High rows as well. **Time the steady state, not the first run** — the first build of a player on a machine is a cold IL2CPP build of the whole host project, and the first launch pays for shader and driver caches that every later one reuses. Neither recurs after the first run |

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
| P1 | — | Capture with `lightCullShader` assigned and again with it cleared | **Not one pixel different.** The cull decides which lights a tile iterates, never what they contribute, so any visual change means it is dropping a light that reaches the tile and the frametime saving is being paid for in wrong pixels. Measured 2026-08-24: bit-identical, 0 pixels differing. This is the image half of P1 in **Cost and culling**, not a second row that shares the name |
| I3 | — | **Sensitivity.** Capture a frame, shift it by one pixel, compare | Detected. Measured: max 0.165, 449 pixels differing. Seeded by shifting a real captured frame rather than by a debug keyword in the volumetric shader — the claim is about what the comparator resolves, and this exercises exactly that without adding surface to shipped code |
| I4 | — | **Specificity.** Capture the same unchanged scene twice | **Identical.** Without this row the sensitivity row above is satisfied by a comparator that calls everything different, which is exactly as useless as one that calls everything the same. Measured: bit-identical, which also says the capture freeze works |
| I1 | — | Compare against this machine's last capture. **Seed it from the same shape of run you will verify with — a full suite run, normally** | At most **0.25%** of the frame differing, which is the budget the row allows for the edge scatter depth priming leaves behind and nothing larger. A size mismatch is refused separately, since it means nothing was compared rather than that nothing moved. Past the budget the row says what moved and writes the images. The first run on a machine seeds the stored frame and reports inconclusive; delete the stored image to re-seed after an intended change |
| I2 | — | Compare against a reference frame in `VRSL_PERF_HOME` | Identical **on the machine that took the reference**. Expect a difference anywhere else and treat it as hardware, not regression: a frame taken on another GPU fails here while I1 passes in the same run. Skips cleanly when the variable is unset, which is how a routine run should be made |
| I5 | — | A manager with every wiring field cleared, bounced so the clear takes effect, repaired, and bounced again | **The same image as one wired by the prefab.** This is the only row that can make the claim: comparing GUIDs proves resolution picked the assets the table names, never that the table names the right ones. Rendering proves it, because a wrong asset in any of them shows: the wrong compute lights nothing, the wrong lighting shader puts nothing on surfaces, the wrong prepass makes every surface flat grey. **Bounced twice on purpose**: the cull resolves its shader in its constructor and the manager builds its volumetric material once and keeps it, so without the first bounce the row compares two correctly wired managers and passes whatever resolution did. Any row that changes a shader field has to drop the cached materials for the same reason |

**Seed and verify in the same shape of run.** The host can change rendering state partway
through a run (it brings the GPU Resident Drawer up with its quality tier), so the rig
holds the drawer off, and depth priming and MSAA, for every render. A filtered run and a
full one then capture the same frame.

**What a frozen capture holds.** `FreezeForImageCapture` aims the beams at the floor at
their authored cone at an intensity of 60, in a frame nothing else lights, and the capture
helpers drive the source with the `Fixtures` pattern held still, every dimmer at full, so
what an image row compares is the package's light. Stored captures and reference frames
are filed under a name carrying the rig's `CaptureVersion` (`rig-default-v2` now), so a
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

**The editor's batch mode never captures a sweep, on purpose.** It has no GPU clock, so a
capture taken there produces CPU-basis numbers nobody should quote in a results table. The
sweep player gets one by running the matrix in a standalone player, the only way to a
GPU-basis matrix nobody has to sit through. The capture loop is shared with the editor
window, and the verdict comes from the same `VRSLBaseline.Compare` the window calls, so
none of the three can disagree.

The build is the slow half and is incremental afterwards, so a before-and-after costs one
full build and one quick one; a second capture of an unchanged package reuses the player.
Only the build needs the editor closed. The player holds no project lock, so a capture can
run beside an open editor.

**A player run and an editor run are never compared.** `Matches` refuses on `context`, so
the two form separate lineages with separate noise floors, and `baseline.json`, an editor
run, is refused by a player candidate rather than reported against it. That is the
intended behaviour: the difference between them is worth keeping visible.

The comparison exits 0 when nothing regressed, 1 when something did or the inputs are
unusable, and 2 when it refuses because the two runs are from different machines.
Refusing rather than failing matters there, since exiting 1 would train whoever reads the
gate to pass `-force` by reflex.

**A refusal writes a verdict line too**, reading zero rows, with the `REFUSED` line above
it saying why, so a wrapper that checks for the verdict line before `REFUSED` doesn't read
a refusal as a regression. Check it by hand whenever that path changes, because no
automated row covers it (the entry point ends in `EditorApplication.Exit`). Compare the
reference run against a copy of itself with `environment.graphicsDevice` altered: expect
`REFUSED`, a zero-row verdict line, and **exit 2**. This is the ordinary outcome of the
defaulted form on any machine that is not the reference machine, not an edge case.

**A sweep measures nothing unless four things are true.** It must render at a fixed size large enough to have work
in it — at a Game view's 964x672 the GPU frame did not move at all as lights per tile
went from 9 to 47. Its DMX source must be assigned to the manager explicitly, because
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
frame time of exactly zero there, with `enableFrameTimingStats` already on in the host
project. The harness falls back to the CPU difference, says so in the run's notes, and marks
every affected row's cost basis `CPU` so a CPU figure is never quoted as a GPU one. Anything
whose claim is about GPU cost therefore cannot be closed headlessly — run it from the editor
window or a player.

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
| D1 | Both | Priming `Forced`, Forward+, moving heads under a cue | Every fixture body renders at every pan and tilt, no flicker and no partial geometry. A body that blinks out as it rotates is a depth pass not following the forward one. **Passed 2026-08-31** by eye, AudioLink example scene on `DesktopRenderer`: ForwardPlus, priming Forced, MSAA 2x on a URP modified to prime multisampled cameras (see D4), so priming was genuinely running |
| D2 | Both | Priming `Disabled`, same scene | Identical result, within 1.0% of pixels and 0.5 points of lit fraction. **Passed 2026-08-31** at MSAA 1: 1761 px of 262144 differing (0.672%), lit fraction 62.89% against 62.99%; 99% of the differing pixels are off by one value of 255, the scatter an `Equal` test leaves at edges. A run at 2x is still to be made (the rig takes a sample count since 2026-09-03). **The automated half reports `Inconclusive` when the two captures come back bit-identical**, because priming then never varied. It doesn't predict from MSAA or rendering mode whether priming will run, since those conditions live in the URP a project ships |
| D3 | Both | Priming `Forced`, a fixture faded to black | A body that vanishes from colour while still writing depth punches a hole in the scene. **Only one fixture can do this.** The collapse to degenerate geometry lives only in the `PROJECTION_YES` and `VOLUMETRIC_YES` branches of `VRSL-StandardMover-Vertex.cginc`, and those meshes draw at 3001 and 3002 and write no depth; the fixture-body branch has no collapse and always draws, which is correct for a housing. **The exception is the shader-driven discoball** (`VRSL-DMX-Static-DiscoBall-H-1CH` and `-V-1CH`, shader `VRSL-Discoball`), whose material is opaque, alpha-tested and `_ZWrite` 1 at queue 2451 with no `DepthOnly` or `DepthNormals` pass, so under priming it is culled outright. The example scenes use the realtime discoball instead; `Validate Renderer Setup` reports the shader-driven one in any scene that places it. **Open limitation, not a pass** |
| D4 | Both | Priming `Forced`, MSAA 4x | **Check which of two rows this is before reading it.** Stock URP declines to prime a multisampled camera, so `Forced` plus MSAA renders as though priming were `Disabled` and the row only shows that raising MSAA breaks nothing. A URP modified to prime anyway (Basis shipped one until September 2026, marked `BASISVR MODIFIED` in `IsDepthPrimingEnabledRenderGraph`) makes it a genuine priming test; S13 says which a run hit. **Covered at MSAA 2x on 2026-08-31** by D1's run on such a URP. **4x is not tested**: reaching it means editing the host project's pipeline asset, and this package doesn't change a project's renderer settings |
| D5 | Both | A scene with no lights at all, of any type | Surfaces still light and beams still render. Depth comes from the pipeline; VRSL needs no light to obtain it, and the old depth-light requirement is gone |
| D6 | — | **Suite, passed 2026-08-31 on its first run.** `VRSLDepthPolicyReport.LayerMaskVerdict` across five masks: a fixture layer excluded under priming `Forced`, the same mask with priming off, the layer included, no fixtures at all, and an unreadable mask | The first fails and **names the layer** — "a layer is excluded" leaves an author to find which of thirty-two. The other four are what make it mean anything: priming off is reported without counting against the verdict, an including mask reports nothing so a check that flagged everything cannot pass, no fixtures reads `NOT CHECKED` rather than as a clean bill, and an unreadable mask reports instead of throwing or passing in silence. **Automated rather than done by hand**: reaching the same verdict through the menu item needs a real `UniversalRendererData` carrying a real mask, which means editing the consuming project's pipeline asset, and this package does not change a project's renderer settings even briefly |

**Frame time is unchanged by the depth policy.** A 30-row player sweep before and after,
RTX 3090, IL2CPP, 1920x1080, priming Forced, 2026-08-31: 29 unchanged, 1 improved, 0 regressed.

**The AudioLink moving heads declare their own depth passes.** Taken from URP's Lit shader,
they were safe only because the AudioLink branch of the shared vertex include has its
rotation commented out; with their own, re-enabling AudioLink pan and tilt doesn't break
them under priming.

### Discoball

`VRSLDiscoballTests` in the suite covers K1 and K2. K3 is by eye.

| # | Path | Scenario | Expected |
|---|---|---|---|
| K1 | D | A `Discoball` fixture from the shipped prefab on the rig, dimmer at full, spin at 90°/s | Its light record reads as the discoball type with no gobo slice, its spin axis is the fixture's up, it is lit, white tint reads white, and the spin phase advances by the authored rate between two reads a dozen frames apart (within a quarter). Proves the config, the compute branch and the clock, on the same path every reader uses |
| K2 | D | The same ball three metres over the rig's floor, movers dark, the surface prepass off so the floor lights as neutral grey; four frames: ball off, no cubemap, a cubemap black but for its downward face, the shipped cubemap | Judged as what the ball adds over the ball-off frame. No cubemap floods over 5% of the frame; the one-face cube and the shipped dots each light more than 0.2% and under 60% of that flood. The one-face cube proves the mask, the shipped asset proves its import. Measured 2026-09-04: 50.4% flooded, 3.6% one face, 1.5% dots. **The prepass is off for a reason**: with it on, what the rig's floor captured as albedo varied run to run (a near-black floor lit as a 3% specular patch, or the neutral fallback flooding at 50%). That was the GPU Resident Drawer batching the floor in some runs and not others (S15), fixed since; the row keeps the prepass off so it judges the ball alone |
| K3 | — | The DMX example scene's discoball (sector 39, on) by eye, then `discoballBeams` on, then a coloured cubemap | Dots sweep the room about the ball's vertical at about 8°/s; with beams on they hang in the haze as thin shafts; a coloured cubemap tints them. Cost: one more light in every tile inside its range, plus a cubemap fetch per raymarch step with beams on. **The look passed by eye 2026-09-04 in the AudioLink example scene.** Beams on and a coloured cubemap not yet judged |

### Volumetrics

`VRSLVolumetricLoopTests` in the suite covers V14 to V17. V11 to V13 want eyes and a
frame capture, and V11 reads its number off `VRSL Diagnostics`.

| # | Path | Scenario | Expected |
|---|---|---|---|
| V1 | — | `Half` (default) | Cones visible, silhouetted correctly against geometry |
| V3 | — | `Off` against `Standard` | Beams present at `Standard` and absent at `Off`, with surfaces still lit at both. Noise follows the level and is on wherever beams are |
| V4 | — | Beam a few metres from the camera, `Half`, with the geometry behind it first near and then far | Cone grain unchanged as the backing surface moves away. **Guards the per-light march span — a shared march loses sample density to whatever sits behind the beam.** It now guards the adaptive step count as well: the count is taken from the light's own span, so a count that grew or shrank with the surface behind the beam would be exactly the fault this row was written for |
| V5 | — | Several cones overlapping, `Half` | Brightness in the overlap is the sum of the individual cones; no seam or banding where one cone's span ends |
| V6 | — | Camera inside a cone, then walking out through its edge | No pop or brightness step as the near end of the span crosses the camera. **Exercises the midpoint selection in `VRSL_NarrowSpanToCone`** |
| V7 | — | Narrow cone (small `spotAngle`), viewed side-on close to the fixture head | Smooth gradient, no dot-screen or weave pattern. **Worst case for span tightness — the cone is narrowest relative to its bounding sphere here** |
| V9 | — | `Standard` against `High` on a wide cone at long throw | `High` is smoother where stepping is visible at all. This is the case the level exists for: what governs stepping is metres of cone per step, so wide cones, long throws and dense haze show it first |
| V10 | — | Cone edges at a grazing angle | Soft feather to the outer angle, no hard rim. **A hard rim means the span is clipping before the attenuation has faded**, or that the visibility bound is skipping lights it should march. The bound is `VRSL_VOL_MIN_CONTRIB` in the volumetric shader; set it to 0 and re-run to tell the two apart |
| V11 | — | One fixture, walked away from the camera so its cone fills less and less of the view ray. `VRSL Diagnostics` on the manager at each distance | The cone keeps the same grain at every distance while the diagnostics' steps per light fall, never below 4 and never above the level's ceiling. Judged from that number, not by eye alone: the count is the span over the level's spacing, and a cone that fills less of the ray is a shorter span |
| V12 | — | A rig of fixtures all pointed away from the camera so only their falloff tails reach it, against the build before the bound | Frame time falls, with no visible difference in the image. `VRSL Diagnostics` reports a non-zero share of lights skipped as too faint to see. **If the image differs, the threshold is too eager**: set `VRSL_VOL_MIN_CONTRIB` to 0 and confirm the difference goes with it |
| V13 | — | The same beam with the baked noise texture and with the procedural field, captured as stills and compared. The procedural field is the build before the texture landed | Different grain is acceptable; different density structure is not. The texture is the same function on a lattice that wraps every 16 cells, so within one period it is the procedural field exactly except in the last cell of each axis |
| V14 | — | `VRSLVolumetricLoopTests`: bake the field with the package's own compute and read it back | A 64-cubed repeat-wrapped texture whose texels span most of the range and sit around the middle, and whose texels across the wrap differ by no more than interior neighbours do. A non-periodic bake puts two unrelated lattice points at every wrap, about a third of the range apart, against a few hundredths between interior neighbours |
| V15 | — | `VRSLVolumetricLoopTests`: bake with a compute that has no kernel | The shared 1×1×1 white fallback, so density is unmodulated rather than zeroed, and one warning per session naming the kernel. Releasing a fallback leaves it alone |
| V16 | — | `VRSLVolumetricLoopTests`: collect the raymarch counters on the rig with its movers pointed straight down, at `Standard`, then at `High` | Steps per light marched never below 4 and never above the level's ceiling, and higher at `High` than at `Standard` on the same spans. The second half is what says the count follows the level's spacing rather than sitting at the ceiling |
| V17 | — | `VRSLVolumetricLoopTests`: the same rig, frozen and on the Ramp, with `volumetricIntensity` at `1e-12` | Every light that reaches the loop is skipped by the bound and no step is taken; the count of lights reaching the loop is unchanged from the lit frame, so the bound moved only the verdict. Restoring the intensity restores the march |

V2 and V8 are retired. A suspected upsample artefact is read off the half-res target in a
frame capture.

**A frozen capture pins the raymarch's clock as well.** The dither phase and the haze
scroll follow time by design, and at the step floor of four the dither reaches the
quantised output on a few grazing pixels: two captures of a frozen scene at different
frames differed by one 8-bit step on two or three of them. `FreezeForImageCapture` holds
the clock at zero through the manager's override, beside the strobe, the spin and the
damping. A capture that skips the freeze is comparing dither phases.

**Rows about what is in the air point the movers down.** Free-running, the rig's movers
follow their channels, which under the synthetic pattern points them off sideways at
shallow angles and puts almost no beam in frame (of some ten thousand marched pixels, 326
rays crossed any cone, all grazing an edge). V16 and V17 switch pan and tilt off for that
reason, and image captures aim the beams down through `FreezeForImageCapture`.

**The counters are how V11, V12, V16 and V17 are judged.** `VRSL Diagnostics` prints
steps per light, lights marched per pixel and the share of lights skipped, and a sweep
row carries the same three. Collecting them is one atomic per counter per pixel, so the
diagnostic arms a request on its first call and answers on the next; the benchmark asks
after each timed window so the atomics never land inside a measured frame. A row that
reads a zero here should first ask whether a frame was collected at all.

### Cameras

`VRSLCameraPolicyTests` in the suite covers M5, M7, M8 and M9. M2 to M4 and the main-view
half of M5 want a mirror and eyes; M6 is a pair of player sweeps.

`secondaryCameraMode` is a policy with four values: `Match`, `Reduced` (the default),
`SurfaceOnly` and `Skip`. `Match` is what `Full` was. A camera that renders into a texture
is a secondary camera to the policy whatever it is for, which includes the test rig's own
camera and the sweep's: both register theirs as the player's view, so every row measures the
level it is labelled with, and a row about the policy adds mirror cameras of its own.

| # | Path | Scenario | Expected |
|---|---|---|---|
| M1 | D | Scene with a DMX screen reader | `Log Decoded Fixture Light Data` unaffected by fixture proximity to the reader quad. **Guards additive light corrupting the decode chain** |
| M2 | — | Mirror pointed at the rig, `secondaryCameraMode = Match` | Beams and lighting appear in the mirror, at the scene's level |
| M3 | — | `SurfaceOnly` | Surface lighting in the mirror, no volumetric cones |
| M4 | — | `Skip` | No VRSL lighting in the mirror |
| M5 | — | `Reduced` in a scene at `Standard` with a mirror pointed at the rig. By hand for the look, and `VRSLCameraPolicyTests.M5` in the suite on a mirror camera the rig adds | Beams appear in the mirror at `Low`, visibly cheaper than `Match`, with no missing fixtures and no difference in the main view. Measured 2026-09-03: the main view is pixel-identical across `Match` and `Reduced` (0 px); the mirror renders at `Low` where the scene is `Standard`; its frame differs from `SurfaceOnly` on 15.6% of pixels (the beams are there) and from `Match` by at most 3 of 255 on 0.26% of them; the raymarch counters from the mirror alone march the same lights (22037 of 22037) at fewer steps each, 4.67 against 7.78. The rig's own camera renders into a texture, so it is registered as the player's view (`VRSLCameraFilter.RegisterMainView`) the way a stream camera would be; without that every row would measure `Low` |
| M6 | — | The sweep player run with `-vrsl-mirrors`, on the build before the decode moved once per frame and on the build after, compared with `CompareFromCommandLine` | 18 rows: 50 fixtures inside the cones at `Standard` and `High`, with none, one and three mirrors under each policy. Each row names the camera count, the policy and the level the mirrors rendered at. Against the before build, the rows with mirrors cost no more and the saving grows with the camera count; within a build, `Reduced` sits between `Match` and `SurfaceOnly` at both scene levels. **Read the deltas and the ratios rather than the verdict**: the stored floor over-covers |
| M7 | D | `VRSLCameraPolicyTests.M7`: four cameras rendering every frame (the rig's and three secondary) for ten seconds of captured time, against the same rig with one | Every spinning fixture's phase advances at the rate its channel asks for, as N8 judges it, and the frame of light data (direction, spin, colour) matches the one-camera rig's to 1e-3. **An integrator that has drifted into a per-camera pass advances four times as far** |
| M8 | D | `VRSLCameraPolicyTests.M8`: a mirror toggled through main-only, mirror-only and both across 45 frames, under `Sweep` at a speed that moves every channel far each frame | On every frame the decoded colour equals the bytes the manager uploaded last, whichever cameras rendered, and the mirror's frame is not black on any frame it rendered. A one-frame-old colour is a decode that did not run for that frame's cameras; a black mirror is the grid textures unbound or the light path skipping it |
| M9 | — | `VRSLCameraPolicyTests.M9`: the camera filter as a function | `Match` renders everything at the scene's level; `SurfaceOnly` the same without beams; `Skip` nothing; `Reduced` everything one level below (`High` to `Standard`, `Standard` to `Low`, `Off` and `Low` unchanged). A camera with no target keeps the scene's level under every value. `Low` draws beams at fewer steps and wider spacing than `Standard` with no contact shadows, and is not in the sweep's level list. Reflection probes, registered data readers and cameras rendering into a texture the manager consumes stay skipped |

**The rig holds depth priming per render** (`VRSLDMXRig.Build(priming:)`, Forced unless a
row asks otherwise, put back on dispose), the way S10 and S13 always did for themselves. The
host rewrites the renderer's mode at a moment of its own after start-up, so a row that set
it once measured whatever the host left; D1_D2 passes its mode through.

**The decode runs once per frame, from the first camera VRSL renders.** Nothing in it
depends on the camera, so every camera in a frame reads one buffer; the DMX grid textures
are still published from inside the graph, by an attachment-less pass, because a global
texture set outside it binds as black. M7 and M8 are the rows that catch it drifting into a
per-camera pass.

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
| N5 | D | Source publishing fewer channels than a fixture is patched at — profiling scene, `universes = 1`, so 520 slots against a patch running to 638 | Sectors 40-49 read 0 and go dark, 10 fixtures. They read 0 rather than another fixture's values, which is the point of the row. Sector 39 goes dark too, for a different reason: it spans flat 508-520, so its colour channels sit in the inter-universe padding, which no block covers because no desk can address it. **`dir` is what separates the two**, since both now read zero colour: sector 39's pan channel at flat 508 is a real published slot and gives it a direction of its own, while sectors 40-49 have no pan or tilt to read and must all share one. No real desk can patch a 13-channel fixture at sector 39 — it would straddle the 512 boundary — so treat it as a dead zone rather than a case to make work |
| N6 | D | Profiling scene, source in UniverseSlot mode with `universes = 2`. Compare fixture (000) at absolute channel 1 against fixture (040) at 521 | Both decode **identically** — rgb `0.027, 0.031, 0.035`. Legacy sector 40 is the first slot of the second universe, so it must read what sector 0 reads. Reading `0.059, 0.063, 0.067` instead means the source packed 512 to a universe and every fixture past the first universe is 8 channels early |
| N7 | D | Add one fixture with `useLegacySectorMode = false`, `dmxUniverse = 2`, `dmxChannel = 1`, same source and pattern | It decodes the same as fixture (040) above. Confirms the two addressing modes agree on where universe 2 begins, since `1 + 520` and `40 * 13 + 1` must both land on flat 521 |
| N9 | D | Profiling scene, channel source on Ramp, `universes = 4`, manager strobe left on Static. `Log Decoded Fixture Light Data` three or four times | Judge on the **`active` flag**, not on printed intensity: under Ramp these fixtures have low dimmers and print `intensity=0.00` while genuinely lit, because the log formats to two decimals. Fixtures whose strobe channel is at or below 0.2 must read `active=1` in **every** sample. The rest must read `active=0` in at least one sample **and** `active=1` in at least one — the second half separates a strobing fixture from one that is dark for some other reason. Which fixtures fall either side depends on the count, since the ramp value at `absChannel + 6` decides it: at 10 fixtures it is a single split, (000)-(003) held and (004)-(009) strobing; at 50 the pattern repeats three times as the ramp wraps at 251. No timing precision is needed, only repetition. At 50 fixtures the patch splits three ways instead of two and the extra group is the point: the medium and high buckets must **disagree** in at least one sample, which is what proves they run at different rates. Ten fixtures cannot test it, because every strobing one falls in the medium bucket. Within a bucket every fixture must read identically at the same instant, wherever it sits on the truss. Beware that `active` also goes to 0 when a fixture is too dim to emit at all (`colorMax > 6/255`): under Ramp at 50 fixtures channel 248 is below that gate and reads 0 in every sample. A strobing fixture alternates, a too-dim one is constant, so repetition separates them |
| N10 | D | Same, then tick the manager's Disable Strobe (or press the control panel's global strobe toggle) | All 50 fixtures read non-zero intensity in every sample. The control panel's toggle sets the same Disable Strobe, so it reaches the compute and the fixture bodies too |
| N11 | D | Profiling scene with movers, channel source on Ramp, `universes = 4`. Log at any point after Play | Fixtures must show **different** `dir` values from each other, tracking their own pan and tilt channels. The movement buffer is allocated zeroed, so had `AdvanceMovement` never dispatched every fixture would read pan and tilt of 0, and since the builder configures all 50 identically apart from sector they would share one direction. Distinct directions therefore prove the kernel ran and populated the buffer. Note that two samples reading **identical** is the settled state and not a failure: with Ramp the target never moves, so the value arrives and stays |
| N12 | D | Set both `movementSmoothingMax` and `movementSmoothingMin` to 0.99, re-enter Play so the buffers zero, then log at about two seconds and again at thirty | How far each fixture has travelled must follow its own smoothness channel, which is channel 13 of its sector — `absChannel + 12`. At the shipped defaults the time constants span only 0.14 s to 0.82 s and everything settles before a person can click twice; at 0.99 they span 0.17 s to 9.6 s, a factor of 56. Expect (016), (017), (018), (036), (037) identical in both samples, and (000), (019), (038), (039) visibly different, with (038) still a tenth short even at thirty seconds. Restore the defaults afterwards |
| N8 | D | Profiling scene, channel source on Ramp, `universes = 4`. `Log Decoded Fixture Light Data` twice about ten seconds apart and compare the `spin` field | Every fixture's spin advances at `4 * (dmx > 0.5 ? dmx - 0.5 : dmx)` rad/s, negative above 0.5, wrapped to +-2pi. Solve the elapsed time from one slow fixture and the same figure must predict all the others, wraps included. Signs must follow the direction bit with no timing needed: above 0.5 spins backwards. All spins reading 0.0000 usually means the fixtures have gobo spin disabled (`cfg.panSettings.w`), not that the integrator is dead |
| N13 | D | Profiling scene, channel source on Ramp, `universes = 4`, tick **Rotate Universes**. Let it run a second, then `VRSL → URP → DMX Config → Validate DMX Channel Buffer` and log the fixtures | PASS on every channel, and every fixture decoding the same colour it does without rotation. Judge on the decoded colour rather than on how many fixtures are lit: this is the same 50-fixture Ramp patch N9 uses, so ch 248 sits below the emit gate and reads `active=0` throughout, and a row demanding all 50 lit would fail on it for an unrelated reason. Only one universe is published per frame, so this is the row that proves the manager keeps what it was last told rather than re-uploading whatever arrived: if the flat space were rebuilt each frame, three universes in four would read 0 and the fixtures above sector 39 would flicker dark. Validating in the first few frames warns rather than fails — the source has not been round the rotation yet |
| N14 | D | Three runs at `movementSmoothingMax` and `movementSmoothingMin` of 0.99, Ramp, `universes = 4`, each sampled at the same elapsed time mid-convergence. **A**: age 0, rotation off. **B**: age 200 ms, rotation off. **C**: age 0, rotation on | **B and C must both reproduce A**, fixture for fixture. A constant age shifts a universe's clock at both ends of the subtraction and so changes no step at all, which makes B an exact repeat of A. Rotation gives a universe one step of four frames instead of four of one — the same total, because the damping and the CRT's pull are both contractions of the same error and contractions commute. Age used as the timestep would put B roughly twelve times ahead and fully settled; a step that advanced only one frame per arrival would leave C four times behind. The row also has to check that something was still moving at the sample point: three settled runs agree whatever the step was. **Do this one through the suite.** Hand-timing is repeatable to a second or two, and the gap that opens between correct and incorrect behaviour in that window is smaller than the gap hand-timing introduces — a manual attempt at this row cannot separate them |
| N15 | D | `VRSLTrussDmxTests` in the suite. No Basis packages needed: the decoder is in the core assembly | The Truss record decoder against records built byte for byte the way Truss builds them: a full universe and a partial run round-trip with their offsets and ages; records accumulate and the arrays grow from below one block's worth; an empty snapshot is valid and appends nothing; a flipped value bit reads as `BadCrc` and leaves what was already appended untouched; each framing fault (`TooShort`, `BadMagic`, `UnsupportedVersion`, `Truncated`, a probe body as `BadPayloadMagic`, `PayloadTooShort`, `UnsupportedPayloadVersion`) is told apart; a block running past its payload refuses the whole record, intact earlier blocks included; CRC-32 matches the `123456789` check value |
| N16 | D | Synthetic DMX Channel Source on Ramp, `universes = 4`, Play, open the **DMX Monitor** | `Reading` shows `Stream data — VRSL_SyntheticDMXChannelSource`. In `Fixture` view every page shows the ramp climbing left to right and wrapping down the rows, the last 8 cells of the final row drawn as padding. Paging through all four universes shows the same pattern, since Ramp is a function of the flat address. Hovering any cell reports a channel one higher than the cell to its left |
| N17 | D | Same, then tick **Verify** | `Verify: all 520 channels read back as published`, on every page. This is N1 against whatever is really playing rather than against the Ramp pattern specifically — a mismatch reports the first differing channel, and a constant offset there is an indexing fault while a neighbouring-looking byte is a packing fault |
| N18 | D | Same, then disable the source component mid-cue | `Reading` switches to `Stream data — nothing is sending it, lights dark` within a frame, with a warning saying why, and the cells read zero; nothing falls back to the grid. Re-enabling switches it back. This is N3 read from the data rather than from the fixtures |
| N19 | D | Set the source's `universes` to 0 while it is registered | The warning says the source is attached but publishing no universes, so the lights are dark. **The row exists because nothing else tells this apart from an empty scene**: `ChannelCount` goes to 0 and the fixtures go dark with no error |
| N20 | D | Monitor open on a scene with no channel source at all, grid CRTs assigned and a DMX-over-video stream playing | `Reading` shows `Video grid — Horizontal` (the manager's layout), and `Grid` names the CRT and its dimensions with the universe count it holds: 3 for the shipped 26×240 Color/Intensity CRT, since a universe is 40 whole rows. Cells track the video. Judge the values against the fixtures rather than against the source: this path reads post-interpolation, which is what the fixtures read, not the raw grid bytes |
| N21 | D | Monitor open and visible, then dock it behind another tab and leave it for a minute | Sampling stops while it is hidden — no dispatch, no readback. Switching back to the tab resumes it with no interaction. Watch it in the profiler or with a frame debugger if you want the claim rather than the absence of a symptom; the mechanism is that the window only asks to be repainted while it is being drawn |
| N22 | D | Overview view, synthetic source on Ramp with `universes = 4` and **Rotate Universes** ticked, then page through the four | Each universe's `last heard N s ago` cycles on its own rather than all four moving together — rotation publishes one universe per frame, so each is a few frames stale in turn. That is what per-universe staleness means: a universe is latched as its packet arrives, and DMX at 44 Hz does not divide into a frame grid. **Watching one go permanently stale needs a source that can stop a single universe, which the synthetic one cannot** — reducing its `universes` reallocates the flat space and drops the page instead. Do that half against a real desk feed |
| N23 | D | `VRSLBasisDmxOverVideoTests` in the suite, against `vrsl-dmx-marching.ts` | The record path against a fixture whose values say which frame they came from: channel c holds `(c - 1 + 5 * frame) % 251`, so the row recovers the offset from the values rather than assuming a frame, and every one of the 1560 channels must agree on it. Nothing dropped, three universes named. The offset walks back to a frame number (five is coprime with 251, so 201 inverts it) and that must be the frame the newest record's header names, or one behind it — the source decodes a record and the manager consumes its blocks on the following frame, so one is the settled state and two means a record went unread |
| N24 | D | Same suite, same fixture, no channel source: `BasisVideoRenderTextureOutput` frames the burnt-in grid strip into the RAW grid RT and the CRT chain decodes it | Every judged channel agrees on one offset, every sector's 13th channel included: each is read from its own cell (G17). Framing the strip without the transpose (the component's own identity default) fails this row, which is the point of having it |
| N25 | D | Same suite, same fixture, **both** feeds present: the picture read on the Horizontal grid with the records on standby, then Stream data chosen and the records read the next frame | The two must name the same frame, less the records that arrived between the reads, within two frames (the CRT's damping). The records are exact on every judged channel the frame after the switch, because standby kept draining them, and the picture is exact on every judged channel (2026-10-11: offset 215, 1276 judged, none outside, worst 1.0). In 0.2.0-rc.6 both feeds were read at one instant, records winning (2026-08-21: offset 215, 0.0 frames apart) |
| N26 | D | Same suite, same fixture, picture only, read back from the RAW grid RT itself rather than through the accessor | Every channel holds its own value, with no exceptions. This is the row that tells a framing fault from a decode-chain one, which N24 cannot — both reach the accessor as "the numbers are wrong". Measured 2026-08-21: worst 1.0 DMX units over 1276 judged channels, which is the TV-range round trip and nothing else. The RT sits one frame ahead of the interpolation CRT, that being the CRT's own latency, so this row recovers its own offset rather than sharing one with N24 |
| N27 | D | `VRSLTrussOscDmxSourceTests.N27` in the suite. No Basis packages needed: records built byte for byte the way Truss builds them, sent to a `VRSLTrussOscDmxSource` on a free loopback port as the `/truss/dmx` message `truss-relay --osc` sends, one blob per message | After 30 records: nothing dropped, 30 datagrams received and none ignored, `UniverseCount` 4, and every one of the 2080 channels read back through the shader equals `RampValue` of its flat address; the newest header carries the lane's own sequence and frame index, equal, under carrier 6. A record naming universe 4 grows the count to 5 exactly once and all 2600 channels read the Ramp. A record with a value bit flipped after its CRC was written is counted dropped with `BadCrc` and the buffer still reads the Ramp. Then four datagrams that are not ours, a message under another address, one with an int argument, a bundle wrapping a real record, and three bytes that are not OSC, all count as ignored, none decodes and none counts as a drop |
| N28 | D | `VRSLTrussOscDmxSourceTests.N28` in the suite: the port already held by another socket when the source enables, then a source whose `listenAddress` is not an address | `Listening` false, `LastError` says the port is held by another program, one error logged naming the address and port, nothing received, and no exception out of enable. The bad address is reported as not an IP address, not as a held port. **By hand, the row that matters for a VJ:** `truss-relay --artnet --osc 127.0.0.1:12100` with a desk sending it Art-Net and nothing else running, a scene with a `VRSL_URPLightManager`, fixtures patched, and a Truss OSC DMX Source added from `VRSL → URP → DMX Config`; press Play. The inspector's Live block shows datagrams and records climbing at the relay's `--osc-rate` and the fixtures follow the desk; the DMX Monitor's `Reading` shows `Stream data — VRSLTrussOscDmxSource`. Stop the relay and the counts stop; a second source on the same port reports the port held rather than fighting for it |
| N29 | D | `VRSLGridDecodeTests.N29`: the rig and the Horizontal flasher, light bar, 15-channel bar, 6x4 strobe and realtime blinder and par, each turned so the side it glows from faces the camera, lit from the synthetic source's Fixtures pattern held still over two universes with a grid of other values bound beside it; then lit from a grid carrying exactly the bytes the source published, with no source. Strobe held | The frames match: 0 px, 2026-10-10. Bodies on the URP include, the lens flares and the rig's mover heads on the Built-in one, and the realtime light all read the buffer while a source publishes and the grid otherwise, from the one count the compute reads too. With the URP include's buffer check forced off, 162 px move (0.06%) against the row's 0.01%. **Upright, these bodies glow away from the capture camera** (the flasher, bar and strobe at the floor, the 15-channel bar backwards), so a frame of them upright shows them black whatever they read |
| N30 | D | **The Built-in include.** `VRSLGridDecodeTests.N30`: N29 for the Horizontal laser, Built-in discoball and both realtime movers, with the rig's movers behind them | The frames match: 0 px, 2026-10-10. The laser's beams, movement and spin, the discoball and the mover heads, which pan and tilt with their beams, follow the source. With the Built-in include's buffer check forced off, 6.1% of the frame moves. **The row also guards when the grid is published**: with the grid textures going out after URP's prepasses and the surface prepass, the heads draw their depth and normals from a grid not yet bound and 216 px move |
| N31 | D | `VRSLGridDecodeTests.N31`, once per include: a test shader draws each include's channel, movement and spin-phase reads for every channel the rig patches, the source on Ramp over two universes with movement smoothing off; once from a grid carrying the manager's values and spin phases with no source, once from the buffers with a grid of other values bound | Every read returns what the manager holds, on both paths, in both includes, spin phase included. Forcing either include's buffer check off fails that include's case |
| N32 | D | **The strobe gate.** `VRSLGridDecodeTests.N32`: a 13-channel light bar patched on sector 10 beside the rig's realtime mover on the same sector, strobing from the synthetic source's Ramp (its strobe channel reads 136, the fastest rate), 60 frames | On every frame the bar glows exactly when the realtime light is open: 31 of 60 open, the bar's brightest pixel 255 on every open frame and at most 18 on every shut one, 2026-10-10. Both take the strobe clock from one sample per frame: the decode publishes the time it hands the compute, so the bar cannot strobe a frame behind the light whenever the frame renders. Taking the bar's clock from `LateUpdate` alone puts it one frame behind in this row, where the rig renders before `LateUpdate` |
| N33 | D | `VRSLGridDecodeTests.N33`: N29's and N30's scenes with a grid of other values bound, lit from the synthetic source on Stream data, the source component switched off two frames before the capture, against the same scenes on Stream data with no source; and the same switch-off on the Horizontal grid with the source on standby, against the scenes that never had a source | Both match. Under Stream data every fixture, shader-driven and realtime, is dark at once, with nothing falling back to the grid; on the grid the standby source going away changes nothing |
| N34 | D | **The compile half.** `VRSLShaderPlatformTests.N34`: every pass that includes the DMX read functions, in the seven shaders that read the channel buffers (Opaque and the Opaque multi-channel bar, laser, lens flare, discoball, both mover bodies), compiled for Android on Vulkan with `ShaderData.Pass.CompileVariant`, plain, instanced, and instanced with multiview, without switching the editor's platform | All 39 variants compile, and each binds at least one of the four channel buffers: the shader model they need is kept on Vulkan rather than the buffer reads being compiled out, 2026-10-10. A mover's shadow pass binds the movement buffer alone, which is all its pose needs. The Opaque surface and the lens flare compile in their 13-channel mode, since they read DMX only under a channel-mode keyword. With the Built-in include's buffer check forced off, every pass on that include is named for binding none. **Not the device half**: whether a Quest reads the buffers in the vertex stage needs a build and a headset |
| N35 | — | The standard 30-row player sweep, the package before shader-driven fixtures followed a source against after, each built from a clean checkout and captured three times, interleaved, on one machine, editor and host project. The sweep's source publishes throughout, so the realtime movers' heads read the buffer after and the grid before | **Measured 2026-10-10** (RTX 3080, Unity 6000.7.0b4, a Basis project): the whole frame with the manager on changes by a median 1.6% against a median run-to-run spread of 2.8%, and the four rows outside their spread are faster; the package's own cost has 11 rows cheaper beyond their spread and none dearer. Emitting fixtures match row for row. With the manager off, which is the grid path for the heads, the frames seen from outside the cones cost 0.016 ms more at 50 fixtures and 0.04 ms at 200; the other rows are within 0.01 ms. Building the player from a working tree whose decode textures are saved as Realtime (as test runs leave them) keeps the decode chain running with the manager off and adds about 0.065 ms to every manager-off frame, so build the two sides from clean checkouts |

### Runtime re-addressing

`VRSLReaddressTests` in the suite. A world script moves a fixture to another address by
setting its address fields and calling `ApplyAddress()` on the fixture, on either
`VRStageLighting_DMX_RealtimeLight` or `VRStageLighting_DMX_Static`. Without the call a
realtime light's body keeps glowing to the old address, and its beam follows only once
something else makes the manager re-upload its fixture data. The par and blinder bodies
move with their beams too (J4).

| # | Path | Scenario | Expected |
|---|---|---|---|
| R1 | D | The rig on Ramp over two universes; one fixture moved from sector 3 to sector 10, another from sector 5 to universe 2 channel 1, each through `ApplyAddress()` and no manager call | From the second frame each beam decodes the colour of the fixture already at its new address, and every body renderer's property block carries the new `_DMXChannel` (131 and 521). Passed 2026-10-10; fails on the colour with `ApplyAddress()` not telling the manager, and on the body with it not redriving the body |
| R2 | — | A Horizontal flasher (a `DMX_Static` with two renderers) moved to universe 2 channel 27, then to sector 3 in single-channel mode on channel 4 | Both renderers read `_DMXChannel` 547, then 44. Passed 2026-10-10; fails with `ApplyAddress()` empty |
| R3 | D | The rig on Ramp with strobe disabled, every fixture's `ApplyAddress()` called once to settle, then again with nothing changed | Colour and intensity bit-identical across the second call for all 50 fixtures; every body still on its own sector's channel. Passed 2026-10-10 |

### Par and blinder bodies

`VRSLParBlinderBodyTests` in the suite. Every DMX par and blinder lists its body, and a blinder
its lens flare, in `fixtureShellRenderers`, so the realtime light drives them from the
fixture's address with tint 2; the 5-channel ones set `use5ChannelMode`. A renderer left off
the list glows at full whatever the desk sends, which is what to suspect when a par lens stays
lit at black.

| # | Path | Scenario | Expected |
|---|---|---|---|
| J1 | D | The ten DMX par and blinder prefabs (13-channel Horizontal, Legacy, Vertical; 5-channel Horizontal, Vertical), as assets and once instantiated | Every renderer is in `fixtureShellRenderers`, the tint is (2, 2, 2), `use5ChannelMode` is set on exactly the 5-channel ones; on wake each listed renderer's block carries `_EnableDMX` 1, the fixture's `_DMXChannel` and the tint. Passed 2026-10-11; fails with the flare dropped from one blinder's list |
| J2 | D | The Horizontal 13- and 5-channel par and blinder, turned to the camera, strobe off and their cast light off (its beam points at the camera in the same colour), every channel at 0 and then dimmer and red at full; read from a painted Horizontal grid and from a channel source; measured on a half-float capture, since a lit body is far above 1 in every channel and clips to white in 8 bits | At 0 every body and flare peaks near 0.005 (linear); lit they peak at 170 to 490 with red leading green and blue by 15 to 60 times, the same from either input. Passed 2026-10-11; fails on the unwired prefabs (peak 3940 at 0) |
| J3 | D | The four 5-channel pars and blinders on Ramp, one universe | Each decodes its colour from +1 to +3 of its address. Passed 2026-10-11; fails with the flag cleared on one blinder |
| J4 | D | A Horizontal 13-channel par, set up as in J2, moved from sector 3 (all 0) to sector 10 (dimmer and red full) through `ApplyAddress()` | The body renders dark before the call (peak about 0.005) and red after it (peak 365, red leading 58 times), its block carries channel 131, and the beam decodes sector 10's red. Passed 2026-10-11; fails on the unwired par, and with `ApplyAddress()` leaving the body on sector 3 even when the block check is taken out |
| J5 | D | **By eye.** A par and a blinder in the editor, body and flare, at black, half, full and strobing | The glow reads as a lamp at tint 2 and the flare reads well at its new curve and tint. **Passed 2026-10-11** by eye in a Basis project: the Horizontal 13- and 5-channel par and blinder beside as-shipped copies (shell list emptied), lit by the synthetic source's Sweep (every channel from 0 to 255 and back in about 20 s), strobe off and then on for 10 s; the bodies sat dark at black where the copies glowed |

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
| B10 | D | `VRSLBasisUserDataTests.B10_*` in the suite: `truss-dmx-ramp-transcoded.ts` (the intact fixture re-encoded with x264) and `truss-dmx-ramp-stripped.ts` (remuxed through `filter_units=remove_types=6`) | Both: the picture plays, `RecordsDecoded` and `RecordsDropped` stay at 0, `LastResult` stays `Ok`, `UniverseCount` stays at `minimumUniverses`. The lane is absent, not damaged, and nothing says otherwise. A rewritten-SEI path reads as B9 does (`BadCrc`/`BadMagic`, dropped count climbing). **By hand, and the part that matters for a show:** the same check against the real CDN you mean to use, with `logDrops` on; a provider that transcodes gives you the B10 reading and is not usable for this. **VRCDN, 2026-08-21: remuxes.** Published through `ingest.vrcdn.live` with `truss-relay --artnet` and read back from `https://stream.vrcdn.live/live/<name>.live.ts`: `truss-detect` 449 of 449 records intact, none corrupt or rewritten, 99 ms median latency; B11 green against that URL with the same assertions as the fixture rows |
| B11 | D | `VRSLBasisUserDataTests.B11` in the suite, run only with `VRSL_TRUSS_LIVE_URL` set: a live lane (Art-Net sender → `truss-relay --artnet` → `mediamtx` → RTSP) carrying the same Ramp over five universes | Over five seconds the record rate sits at the video's frame rate (20-45/s accepted), nothing is dropped on a remuxing path, `UniverseCount` is 5 and all 2600 channels read the Ramp. Same assertions as the fixture rows, so the two agree or the difference is the path. Green 2026-08-21 against a loopback lane and against VRCDN's TS egress |

### VR (single-pass instanced)

| # | Path | Scenario | Expected |
|---|---|---|---|
| X1 | — | Any lit scene in headset | Lighting identical in both eyes |
| X2 | — | Volumetric cones in headset | Cones in the same world position in both eyes |
| X3 | — | Diagnostics, tile culling active | No vertical mirroring of lighting between eyes. Mirroring points at the `renderIntoTexture` inference in `VRSLTileCullPass`; clearing `lightCullShader` isolates it |
| X4 | — | Fixture body meshes in headset | Visible in both eyes |
| X5 | — | A blinder or flasher lens flare, a laser and the discoball in headset, DMX and AudioLink prefabs | Each visible in both eyes at the same world position, the flare quad facing each eye. **Written 2026-09-04, not yet run in a headset:** the stereo-instanced variant is compiled for all six shaders, and the lens flare and the discoball render on desktop (by eye, 2026-09-04); whether both eyes draw them is the row and needs a headset |

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
