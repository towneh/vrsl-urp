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

The rows in **DMX channel buffer** below are implemented as PlayMode tests and should be
run rather than performed by hand:

```
.compilecheck/tests.sh          # headless; needs the editor closed and a real GPU
```

or from the Test Runner window, assembly `Towneh.VRSL.URP.Tests`. The suite builds its own
rig in code, so it needs neither the profiling sample nor a hand-authored scene.

A second assembly, `Towneh.VRSL.URP.Basis.Tests`, exists only when `com.basis.mediaplayer` is
in the project and holds the Basis integration rows (B7-B9 and B11 below). Those
play a real stream: the fixtures are hosted at `https://mr.town/vod/`, and `VRSL_TRUSS_FIXTURES`
(a directory or a URL base) points the rows somewhere else. `tests.sh` runs both assemblies.

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
| `hit the 64-light cap` | Fixtures are being silently dropped in dense tiles. |
| `Surface prepass: normals only` | Everything lights as neutral grey; albedo isn't reaching the BRDF. |
| `NOT IN PLAY MODE` | Nothing is initialised. Enter play mode; shader assignment is still reported. |
| `Fixtures: NONE FOUND` | The manager collected nothing on enable — wrong manager for the scene, or fixtures inactive/added since. |

### 4. The DMX monitor

`VRSL → URP → DMX Monitor`. Every channel of a universe as a cell shaded by value, live,
from whichever source is driving the scene. Read-only — it never writes a channel.

**Play mode only**, for the same reason as the diagnostics report.

Reach for it before the report when the question is about the *data* rather than about
the rendering. It separates causes the lights cannot:

| What you see | Means |
|---|---|
| Header names a channel source | Fixtures are reading published bytes, not the grid |
| Header warns a source is registered but mute | The source published no universes, so the manager stopped and the fixtures fell back to the grid. Identical to having no source at all, from the lights |
| `Change` ramp completely dark | No channel is *moving*. Not a liveness test on its own — a source republishing unchanged values reads the same as one that stopped. Confirm against `last heard N s ago` |
| `last heard N s ago` climbing | That universe has stopped arriving; its last values are still on screen |
| Diagonal shear in `Fixture` view | The patch is offset. One row is one 13-channel fixture, so an off-by-one is visible as a shear rather than as plausible colour |
| Non-zero in a padding cell | A stride fault in the manager's scatter, or something writing the flat space outside it. **Not** an overrunning block — `ScatterBlocks` clamps every run to the universe's usable slots, so a block cannot reach these addresses however it is malformed |

**What it doesn't cover:** on the video path it decodes through the compute shader's
`IndustryRead`, so a legacy-mode or nine-universe grid does not read correctly there. The
window states this whenever it is on the video path. The buffer path is unaffected.

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

### Surface accuracy

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

### Occlusion

| # | Path | Scenario | Expected |
|---|---|---|---|
| O1 | — | `contactShadowStrength` 0 → 1, avatar standing in a beam | Shadow appears at its feet |
| O2 | — | Walk the occluder towards the edge of frame | Shadow fades out as it leaves screen. **Expected** — screen-space only |
| O3 | — | Wall between fixture and a surface across the room | Still lit through the wall. **Expected** — no light-perspective shadows |
| O4 | — | Raise `contactShadowSteps` on thin geometry | Less light leaking through |

### Cost and culling

| # | Path | Scenario | Expected |
|---|---|---|---|
| P1 | — | Clear `lightCullShader`, compare | Identical image, worse frametime. Any visual change means the cull is wrong |
| P2 | — | Diagnostics with fixtures spread across the venue | Average lights/tile well below fixture count |
| P3 | — | Profiling sample sweep, 10 → 25 → 50 → 100 → 200 | Frametime scales sub-linearly with fixture count |
| P4 | — | `InsideCones` vs `OutsideCones` camera variants | Inside is the worst case; the gap shows culling working |

### Volumetrics

| # | Path | Scenario | Expected |
|---|---|---|---|
| V1 | — | `Half` (default) | Cones visible, silhouetted correctly against geometry |
| V2 | — | `Full` | Same shape, no upsample fringing, ~4× the per-pixel cost |
| V3 | — | Toggle `volumetricUseNoise` | Density becomes patchy; no cost when off |
| V4 | — | Beam a few metres from the camera, `Half`, with the geometry behind it first near and then far | Cone grain unchanged as the backing surface moves away. **Guards the per-light march span — a shared march loses sample density to whatever sits behind the beam** |
| V5 | — | Several cones overlapping, `Half` | Brightness in the overlap is the sum of the individual cones; no seam or banding where one cone's span ends |
| V6 | — | Camera inside a cone, then walking out through its edge | No pop or brightness step as the near end of the span crosses the camera. **Exercises the midpoint selection in `VRSL_NarrowSpanToCone`** |
| V7 | — | Narrow cone (small `spotAngle`), viewed side-on close to the fixture head | Smooth gradient, no dot-screen or weave pattern. **Worst case for span tightness — the cone is narrowest relative to its bounding sphere here** |
| V8 | — | Compare `Half` and `Full` on the same view | Same structure in both. A pattern present in `Full` is in the march; one only in `Half` is in the downsample or upsample |
| V9 | — | Drop `volumetricStepCount` towards its minimum | Degrades to visible stepping gradually, and far lower than the default before it shows |
| V10 | — | Cone edges at a grazing angle | Soft feather to the outer angle, no hard rim. **A hard rim means the span is clipping before the attenuation has faded** |

### Cameras

| # | Path | Scenario | Expected |
|---|---|---|---|
| M1 | D | Scene with a DMX screen reader | `Log Decoded Fixture Light Data` unaffected by fixture proximity to the reader quad. **Guards additive light corrupting the decode chain** |
| M2 | — | Mirror pointed at the rig, `secondaryCameraMode = Full` | Beams and lighting appear in the mirror |
| M3 | — | `SurfaceOnly` | Surface lighting in the mirror, no volumetric cones |
| M4 | — | `Skip` | No VRSL lighting in the mirror |

### DMX channel buffer

Values published as bytes rather than decoded from the pixel grid. A scene with no
channel source is unaffected, so N4 is the regression that matters most.

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
| N1 | D | Synthetic DMX Channel Source in Ramp mode, Play, then `VRSL → URP → Validate DMX Channel Buffer` | PASS: every channel read back through the shader matches what was published. A constant offset in the reported channel is an indexing error; a value that looks like a neighbouring byte is a packing error |
| N2 | D | Same source in Fixtures mode, no video and no capture camera running | Fixtures **light** from the buffer alone: `Log Decoded Fixture Light Data` shows non-zero intensity and the buffer's colours. Colour, intensity, cone, gobo selection, movement, spin and strobe all read the buffer, so the fixtures light **and** move from it with no CRT chain involved |
| N3 | D | Fixtures mode, then disable the source component mid-cue | Fixtures fall back to the CRT chain within a frame rather than latching or going dark |
| N4 | D | No channel source anywhere in the scene | Decode is bit-identical to the texture path — this is every existing scene |
| N5 | D | Source publishing fewer channels than a fixture is patched at — profiling scene, `universes = 1`, so 520 slots against a patch running to 638 | Sectors 40-49 read 0 and go dark, 10 fixtures. They read 0 rather than another fixture's values, which is the point of the row. Sector 39 goes dark too, for a different reason: it spans flat 508-520, so its colour channels sit in the inter-universe padding, which no block covers because no desk can address it. **`dir` is what separates the two**, since both now read zero colour: sector 39's pan channel at flat 508 is a real published slot and gives it a direction of its own, while sectors 40-49 have no pan or tilt to read and must all share one. No real desk can patch a 13-channel fixture at sector 39 — it would straddle the 512 boundary — so treat it as a dead zone rather than a case to make work |
| N6 | D | Profiling scene, source in UniverseSlot mode with `universes = 2`. Compare fixture (000) at absolute channel 1 against fixture (040) at 521 | Both decode **identically** — rgb `0.027, 0.031, 0.035`. Legacy sector 40 is the first slot of the second universe, so it must read what sector 0 reads. Reading `0.059, 0.063, 0.067` instead means the source packed 512 to a universe and every fixture past the first universe is 8 channels early |
| N7 | D | Add one fixture with `useLegacySectorMode = false`, `dmxUniverse = 2`, `dmxChannel = 1`, same source and pattern | It decodes the same as fixture (040) above. Confirms the two addressing modes agree on where universe 2 begins, since `1 + 520` and `40 * 13 + 1` must both land on flat 521 |
| N9 | D | Profiling scene, channel source on Ramp, `universes = 4`, manager strobe left on Static. `Log Decoded Fixture Light Data` three or four times | Judge on the **`active` flag**, not on printed intensity: under Ramp these fixtures have low dimmers and print `intensity=0.00` while genuinely lit, because the log formats to two decimals. Fixtures whose strobe channel is at or below 0.2 must read `active=1` in **every** sample. The rest must read `active=0` in at least one sample **and** `active=1` in at least one — the second half separates a strobing fixture from one that is dark for some other reason. Which fixtures fall either side depends on the count, since the ramp value at `absChannel + 6` decides it: at 10 fixtures it is a single split, (000)-(003) held and (004)-(009) strobing; at 50 the pattern repeats three times as the ramp wraps at 251. No timing precision is needed, only repetition. At 50 fixtures the patch splits three ways instead of two and the extra group is the point: the medium and high buckets must **disagree** in at least one sample, which is what proves they run at different rates. Ten fixtures cannot test it, because every strobing one falls in the medium bucket. Within a bucket every fixture must read identically at the same instant, wherever it sits on the truss. Beware that `active` also goes to 0 when a fixture is too dim to emit at all (`colorMax > 6/255`): under Ramp at 50 fixtures channel 248 is below that gate and reads 0 in every sample. A strobing fixture alternates, a too-dim one is constant, so repetition separates them |
| N10 | D | Same, then tick the manager's Disable Strobe (or press the control panel's global strobe toggle) | All 50 fixtures read non-zero intensity in every sample. The control panel half also checks that the toggle reaches the compute at all: before this was wired it only wrote `_DisableStrobe` to the CRT materials, so on the buffer path it did nothing |
| N11 | D | Profiling scene with movers, channel source on Ramp, `universes = 4`. Log at any point after Play | Fixtures must show **different** `dir` values from each other, tracking their own pan and tilt channels. The movement buffer is allocated zeroed, so had `AdvanceMovement` never dispatched every fixture would read pan and tilt of 0, and since the builder configures all 50 identically apart from sector they would share one direction. Distinct directions therefore prove the kernel ran and populated the buffer. Note that two samples reading **identical** is the settled state and not a failure: with Ramp the target never moves, so the value arrives and stays |
| N12 | D | Set both `movementSmoothingMax` and `movementSmoothingMin` to 0.99, re-enter Play so the buffers zero, then log at about two seconds and again at thirty | How far each fixture has travelled must follow its own smoothness channel, which is channel 13 of its sector — `absChannel + 12`. At the shipped defaults the time constants span only 0.14 s to 0.82 s and everything settles before a person can click twice; at 0.99 they span 0.17 s to 9.6 s, a factor of 56. Expect (016), (017), (018), (036), (037) identical in both samples, and (000), (019), (038), (039) visibly different, with (038) still a tenth short even at thirty seconds. Restore the defaults afterwards |
| N8 | D | Profiling scene, channel source on Ramp, `universes = 4`. `Log Decoded Fixture Light Data` twice about ten seconds apart and compare the `spin` field | Every fixture's spin advances at `4 * (dmx > 0.5 ? dmx - 0.5 : dmx)` rad/s, negative above 0.5, wrapped to +-2pi. Solve the elapsed time from one slow fixture and the same figure must predict all the others, wraps included. Signs must follow the direction bit with no timing needed: above 0.5 spins backwards. All spins reading 0.0000 usually means the fixtures have gobo spin disabled (`cfg.panSettings.w`), not that the integrator is dead |
| N13 | D | Profiling scene, channel source on Ramp, `universes = 4`, tick **Rotate Universes**. Let it run a second, then `VRSL → URP → Validate DMX Channel Buffer` and log the fixtures | PASS on every channel, and every fixture decoding the same colour it does without rotation. Judge on the decoded colour rather than on how many fixtures are lit: this is the same 50-fixture Ramp patch N9 uses, so ch 248 sits below the emit gate and reads `active=0` throughout, and a row demanding all 50 lit would fail on it for an unrelated reason. Only one universe is published per frame, so this is the row that proves the manager keeps what it was last told rather than re-uploading whatever arrived: if the flat space were rebuilt each frame, three universes in four would read 0 and the fixtures above sector 39 would flicker dark. Validating in the first few frames warns rather than fails — the source has not been round the rotation yet |
| N14 | D | Three runs at `movementSmoothingMax` and `movementSmoothingMin` of 0.99, Ramp, `universes = 4`, each sampled at the same elapsed time mid-convergence. **A**: age 0, rotation off. **B**: age 200 ms, rotation off. **C**: age 0, rotation on | **B and C must both reproduce A**, fixture for fixture. A constant age shifts a universe's clock at both ends of the subtraction and so changes no step at all, which makes B an exact repeat of A. Rotation gives a universe one step of four frames instead of four of one — the same total, because the damping and the CRT's pull are both contractions of the same error and contractions commute. Age used as the timestep would put B roughly twelve times ahead and fully settled; a step that advanced only one frame per arrival would leave C four times behind. The row also has to check that something was still moving at the sample point: three settled runs agree whatever the step was. **Do this one through the suite.** Hand-timing is repeatable to a second or two, and the gap that opens between correct and incorrect behaviour in that window is smaller than the gap hand-timing introduces — a manual attempt at this row cannot separate them |
| N15 | D | `VRSLTrussDmxTests` in the suite. No Basis packages needed: the decoder is in the core assembly | The Truss record decoder against records built byte for byte the way Truss builds them: a full universe and a partial run round-trip with their offsets and ages; records accumulate and the arrays grow from below one block's worth; an empty snapshot is valid and appends nothing; a flipped value bit reads as `BadCrc` and leaves what was already appended untouched; each framing fault (`TooShort`, `BadMagic`, `UnsupportedVersion`, `Truncated`, a probe body as `BadPayloadMagic`, `PayloadTooShort`, `UnsupportedPayloadVersion`) is told apart; a block running past its payload refuses the whole record, intact earlier blocks included; CRC-32 matches the `123456789` check value |
| N16 | D | Synthetic DMX Channel Source on Ramp, `universes = 4`, Play, open the **DMX Monitor** | Header reads `Channel buffer — VRSL_SyntheticDMXChannelSource`. In `Fixture` view every page shows the ramp climbing left to right and wrapping down the rows, the last 8 cells of the final row drawn as padding. Paging through all four universes shows the same pattern, since Ramp is a function of the flat address. Hovering any cell reports a channel one higher than the cell to its left |
| N17 | D | Same, then tick **Verify** | `Verify: all 520 channels read back as published`, on every page. This is N1 against whatever is really playing rather than against the Ramp pattern specifically — a mismatch reports the first differing channel, and a constant offset there is an indexing fault while a neighbouring-looking byte is a packing fault |
| N18 | D | Same, then disable the source component mid-cue | Header switches to `Video grid — CRT decode chain` within a frame and the cells follow the grid. Re-enabling switches it back. This is N3 read from the data rather than from the fixtures |
| N19 | D | Set the source's `universes` to 0 while it is registered | Header warns that the source is registered but publishing no universes and that the fixtures have fallen back to the grid. **The row exists because nothing else distinguishes this from an empty scene** — the manager calls `StopPublishing`, `ChannelCount` goes to 0, and the fixtures read the grid with nothing said |
| N20 | D | Monitor open on a scene with no channel source at all, grid CRTs assigned and a DMX-over-video stream playing | Header reads `Video grid` and names the CRT and its dimensions with the universe count it holds — 3 for the shipped 26×240 Color/Intensity CRT, since a universe is 40 whole rows. Cells track the video. Judge the values against the fixtures rather than against the source: this path reads post-interpolation, which is what the fixtures read, not the raw grid bytes |
| N21 | D | Monitor open and visible, then dock it behind another tab and leave it for a minute | Sampling stops while it is hidden — no dispatch, no readback. Switching back to the tab resumes it with no interaction. Watch it in the profiler or with a frame debugger if you want the claim rather than the absence of a symptom; the mechanism is that the window only asks to be repainted while it is being drawn |
| N22 | D | Overview view, synthetic source on Ramp with `universes = 4` and **Rotate Universes** ticked, then page through the four | Each universe's `last heard N s ago` cycles on its own rather than all four moving together — rotation publishes one universe per frame, so each is a few frames stale in turn. That is what per-universe staleness means: a universe is latched as its packet arrives, and DMX at 44 Hz does not divide into a frame grid. **Watching one go permanently stale needs a source that can stop a single universe, which the synthetic one cannot** — reducing its `universes` reallocates the flat space and drops the page instead. Do that half against a real desk feed |
| N23 | D | `VRSLBasisDmxOverVideoTests` in the suite, against `vrsl-dmx-marching.ts` | The record path against a fixture whose values say which frame they came from: channel c holds `(c - 1 + 5 * frame) % 251`, so the row recovers the offset from the values rather than assuming a frame, and every one of the 1560 channels must agree on it. Nothing dropped, three universes named. The offset walks back to a frame number (five is coprime with 251, so 201 inverts it) and that must be the frame the newest record's header names, or one behind it — the source decodes a record and the manager consumes its blocks on the following frame, so one is the settled state and two means a record went unread |
| N24 | D | Same suite, same fixture, no channel source: `BasisVideoRenderTextureOutput` frames the burnt-in grid strip into the RAW grid RT and the CRT chain decodes it | Every judged channel agrees on one offset **except** the 13th channel of a sector inside the five ranges `GetDMXValue`'s correction table shifts (90-101, 160-205, 326-404, 676-819, 1339 up), which read the value 13 below their own. The row asserts that exact set rather than a tolerance, and that each of them lands on the row below rather than somewhere arbitrary, so a change to VRSL's table is a failure rather than a silently wider spread. Framing the strip without the transpose — the component's own identity default — fails this row, which is the point of having it |
| N25 | D | Same suite, same fixture, **both** paths live: the records driving the channel buffer while the picture drives the CRT chain, read inside one frame | The two must name the same frame. The rig reads the buffer through the manager's channel count and the picture with that count forced to zero, so `MainChannel` takes its texture branch and both feeds are read at one instant rather than at two moments in the stream. Measured 2026-08-21: both at offset 215, 0.0 frames apart, the record path exact on all 1276 judged channels and the picture path exact bar the 34 shifted 13ths. A non-zero gap here is the CRT's damping or a delivery lag, and the row bounds it at two frames |
| N26 | D | Same suite, same fixture, picture only, read back from the RAW grid RT itself rather than through the accessor | Every channel holds its own value, with no exceptions: VRSL's correction table lives in the accessor, so nothing shifts this side of it. This is the row that tells a framing fault from a decode-chain one, which N24 cannot — both reach the accessor as "the numbers are wrong". Measured 2026-08-21: worst 1.0 DMX units over 1276 judged channels, which is the TV-range round trip and nothing else. The RT sits one frame ahead of the interpolation CRT, that being the CRT's own latency, so this row recovers its own offset rather than sharing one with N24 |

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
| B11 | D | `VRSLBasisUserDataTests.B11` in the suite, run only with `VRSL_TRUSS_LIVE_URL` set: a live lane (Art-Net sender → `truss-relay --artnet` → `mediamtx` → RTSP; `BasisApps/basis-truss-live` brings one up on this machine) carrying the same Ramp over five universes | Over five seconds the record rate sits at the video's frame rate (20-45/s accepted), nothing is dropped on a remuxing path, `UniverseCount` is 5 and all 2600 channels read the Ramp. Same assertions as the fixture rows, so the two agree or the difference is the path. Green 2026-08-21 against a loopback lane (`BasisApps/basis-truss-live`) and against VRCDN's TS egress |

### VR (single-pass instanced)

| # | Path | Scenario | Expected |
|---|---|---|---|
| X1 | — | Any lit scene in headset | Lighting identical in both eyes |
| X2 | — | Volumetric cones in headset | Cones in the same world position in both eyes |
| X3 | — | Diagnostics, tile culling active | No vertical mirroring of lighting between eyes. Mirroring points at the `renderIntoTexture` inference in `VRSLTileCullPass`; clearing `lightCullShader` isolates it |
| X4 | — | Fixture body meshes in headset | Visible in both eyes |

---

## Known gaps — not bugs

Don't raise these; they're documented limitations with reasons in
`Documentation~/URP-Realtime-Volumetric-Lights.md`.

- Light doesn't stop at walls — only contact shadows exist, no light-perspective shadow maps.
- Occluders off screen cast no shadow, and shadows fade as an occluder leaves frame.
- Smoothness and metallic **maps** aren't sampled; only the scalar material properties are.
  A material with a metallic map reads at its smoothness *ceiling*, so it will look glossier
  than it renders.
- No ambient occlusion term. AO models indirect occlusion and would be wrong applied to
  direct light.
- Transparent geometry receives no VRSL light — the passes run after opaques.
- Running the DMX and AudioLink managers on the same camera is unsupported; both write
  `_VRSLLights` and the last one scheduled wins.

---

## Reporting

State the row IDs exercised and their outcome, plus platform and whether it was desktop or
headset. Rows not run should be named as such rather than left implied.

When a change alters the feature surface — a new capability, a removed one, a changed
expectation — update this file in the same branch.
