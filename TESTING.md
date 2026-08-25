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

The **Measurement harness** rows (H1-H6) are in the same assembly and run the same way. They
measure rather than decode, so they are the slow part of a full run.

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

`VRSL → URP → DMX Config → DMX Monitor`. Every channel of a universe as a cell shaded by value, live,
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
| C6 | — | `VRSLManagerLifecycleTests` in the suite: a manager switched off in the inspector loads beside a running one | The running one owns the singleton and drives the light path. A switched-off component still gets `Awake` but never `OnEnable` or `OnDisable`, so a claim made there is never released and the running manager destroys itself as a duplicate — a scene with a manager in it, no lighting, and nothing in the Console |
| C7 | — | `VRSLManagerLifecycleTests` in the suite: two managers, one of them switched on after the other already owns the singleton, then the owner switched off, with a channel source attached | The one still running takes the singleton over, the channel source comes with it, and the light path keeps going. A manager that stood down on being enabled gets no second `OnEnable`, so without a handover the scene is left with an enabled manager, no owner and no lighting — the same silent symptom as C6, reached from the other direction. The source has to travel too: it published to whichever manager held the singleton, and a new owner without one stops publishing on its first frame |
| C8 | — | A scene with a second manager left switched on beside the one that owns the singleton | The owner's DMX data survives. Per-frame work is the owner's alone: a non-owner reaching `UploadChannels` with no source of its own rebinds the channel buffer and zeroes the channel-count global, so it blanks the owner's channels every frame with nothing in the Console. Covered by the ownership gate in `LateUpdate`; **no automated row yet** |

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

### Measurement harness

These judge the instrument rather than the package, and they exist because a measurement
tool that is trusted and wrong is worse than no tool. **Re-run them after any change to how
results are read** — that is the change most likely to break them silently.

`VRSLBenchmarkTests` in the suite. They measure, so they are slower than the rest of it and
they render at 1024 square: at the rig's default 256 there is not enough per-pixel work for
a real regression to clear the noise, and a row that cannot fail under the fault it is
looking for is worse than no row.

| # | Path | Scenario | Expected |
|---|---|---|---|
| H1 | — | **A-M0-1, the null run.** Capture, change nothing, capture again, compare | Every row **unchanged**, and the stated noise floor at or above the spread the two runs actually showed. Judge the guards as much as the verdict: each capture must report the package costing something. A run of zeroes compares as unchanged against anything, so a row of all-zeroes passing is the failure this row exists to catch, not a pass. Measured 2026-08-24 headless: package cost 0.257 and 0.293 ms CPU, delta 0.037 ms against a floor of 0.059 ms |
| H2 | — | **A-M0-2, the seeded regression.** Same capture with `volumetricStepCount` raised to 160 | Two halves. The **counter** half must pass anywhere: steps per light reads the shipped default in the baseline and 160 in the candidate, and the regression is reported with a counter change explaining it. The **timing** half needs a GPU clock and is `Inconclusive` without one, which is neither a pass nor a failure — see the batch-mode note below. `lightCullShader` was the lever first and was rejected: clearing it measured 0.0015 ms while the counters showed the cull working, so it cannot clear the noise floor on either clock, and on a CPU clock it is marginally *cheaper* because it removes a compute dispatch. P1 is the row that clears it now, and it asks a question about pixels rather than about time |
| H3 | — | **A-M0-3.** Three captures of an unchanged scene; measure the spread across all three | The spread must be within a tolerance **declared in code** — 40% of the measured cost, with a 0.05 ms floor for costs too small for a fraction to mean anything. Judged against a declared figure rather than one derived from the same three runs, because a floor taken from the data it adjudicates cannot fail. It is a smoke test for a harness that has stopped working, not a precision certificate: batch mode reproduces poorly and inconsistently, measured at 5.9% of the cost on one run and 33.9% on the next at 48 frames a side. Raising to 160 frames a side brought it to around 10%. Measured 2026-08-24: 0.208, 0.190 and 0.188 ms CPU — a spread of 0.021 ms, which is 10.4% against the 40% allowed. It does get close: another run the same day spread 36.7%, so the tolerance is doing real work rather than sitting far above anything observed |
| H4 | — | Any capture, first one in the process | Must be **discarded**. A session's opening capture runs pinned at exactly the capture delta — both halves at 16.666 ms with an IQR of 0.03 — so the difference cancels to zero while the counters look perfectly healthy. Idle frames do not clear it and neither does a longer warm-up inside the capture; disposing and rebuilding the rig does, which is why the warm-up is a whole capture taken and thrown away |
| H5 | — | Quality `Standard` against quality `Off` | Two halves again. The **counter** half must pass anywhere: steps per light reads 24 at `Standard` and **0** at `Off`, which is the observable that says the volumetric pass is not being enqueued rather than merely being configured down. Clearing `volumetricShader` alone does not do it — the manager builds its material once and keeps it — so a preset that only cleared the field would report volumetrics off while running them at full cost. The **timing** half needs a GPU clock, direction included. It was briefly asserted on either clock, on the strength of one favourable reading of 0.069 ms against a stated 0.021 — then measured `Off` at 0.207 ms against `Standard` at 0.171, dearer by 0.036, with the counters still saying `Off` enqueued no pass at all. H3 explains it: the run-to-run spread on the CPU clock is around 0.055 ms, wider than the gap, so the sign flips as often as not. **A slim gap plus one confirming observation is not a row** |
| H6 | — | `VRSLBenchmarkScene.SetActiveFixtures` at each count in the matrix | The manager must collect exactly the count the row claims. The sweep varies fixture count by activating a subset of one truss rather than rebuilding, and the subset is evenly spaced so the spread is identical at every count — taking the first N would cluster them at one end and change lights per tile, which is a counter the sweep reports. Rounding collisions at small counts are how this goes wrong, and it goes wrong silently. Measured 2026-08-24: exact at 10, 25, 50, 100 and 200 |

### Image regression

Rendered frames compared rather than eyeballed. `VRSLImageRegressionTests` in the suite.

The default comparison is against **this machine's own previous capture**, not a
committed image: two GPUs do not render identically, so a committed reference is a
false-failure machine everywhere except where it was made. Committed references are the
second mode, found through `VRSL_PERF_HOME`, and rows needing one skip with a message
when it is unset — a row that goes red because an environment variable is missing
teaches people to ignore red rows.

Captures freeze everything that integrates over time. Strobe alternates, gobo spin never
settles, and movement damping converges only after a warm-up. Without that freeze a row
compares two frames of the same scene at different moments and reports a difference that
is entirely the clock.

| # | Path | Scenario | Expected |
|---|---|---|---|
| P1 | — | Capture with `lightCullShader` assigned and again with it cleared | **Not one pixel different.** The cull decides which lights a tile iterates, never what they contribute, so any visual change means it is dropping a light that reaches the tile and the frametime saving is being paid for in wrong pixels. Measured 2026-08-24: bit-identical, 0 pixels differing. This is the row that makes the cull trustworthy enough for M3 to build on |
| I3 | — | **A-M0-4, sensitivity.** Capture a frame, shift it by one pixel, compare | Detected. Measured: max 0.165, 449 pixels differing. Seeded by shifting a real captured frame rather than by a debug keyword in the volumetric shader — the claim is about what the comparator resolves, and this exercises exactly that without adding surface to shipped code |
| I4 | — | **A-M0-4, specificity.** Capture the same unchanged scene twice | **Identical.** Without this row the sensitivity row above is satisfied by a comparator that calls everything different, which is exactly as useless as one that calls everything the same. Measured: bit-identical, which also says the capture freeze works |
| I1 | — | Compare against this machine's last capture. **Seed it from the same shape of run you will verify with — a full suite run, normally** | Identical, or the row says what moved and writes the images. The first run on a machine seeds the stored frame and reports inconclusive; delete the stored image to re-seed after an intended change |
| I2 | — | Compare against the committed reference, `VRSL_PERF_HOME` set | Identical **on the reference machine**. Expect a difference anywhere else and treat it as hardware, not regression. Skips cleanly when the variable is unset |

**A reference frame is only valid for the run shape that seeded it.** Seeding from a
filtered run and verifying in a full one reports 1341 pixels differing, identically
every time — deterministic, but a property of what ran first rather than of the
renderer. The difference is a narrow band at the far edge of the floor and the cause is
not yet identified; blanking the DMX grid globals and discarding a warm-up capture were
both tried and changed nothing. Seed and verify the same way and the rows are exact,
which is what the suite does. Worth chasing before anyone relies on these rows across
run shapes.

A failing row writes `-expected`, `-actual` and an amplified `-diff` PNG under
`VRSL-Benchmarks/image-failures/`, because a number cannot distinguish a global
brightness shift from one wrong beam. The difference is amplified 16× — a real
regression is often a handful of 8-bit steps, and an unamplified difference image is a
black rectangle whatever went wrong.

### The headless gate

`.compilecheck/bench.sh` (local, like the other scripts).

```
bench.sh check                              # the gate: harness + image rows
bench.sh compare <baseline> <candidate>     # adjudicate two runs, non-zero on regress
```

**It does not capture a sweep headlessly, on purpose.** Batch mode has no GPU clock, so
a headless capture produces CPU-basis numbers nobody should quote in a results table.
Capture from the editor window; adjudicate here. The verdict comes from the same
`VRSLBaseline.Compare` the window calls, so the two cannot disagree.

`compare` exits 0 when nothing regressed, 1 when something did or the inputs are
unusable, and 2 when it refuses because the two runs are from different machines —
refusing rather than failing, since exiting 1 there would train whoever reads the gate
to pass `-force` by reflex. It fails when the log carries no verdict line, because a
runner that exits successfully having compared nothing is worse than no runner.

**A sweep measures nothing unless four things are true**, and each was found the hard
way rather than reasoned out. It must render at a fixed size large enough to have work
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
manager rebuilds — quality `Off` most of all, which measured identical to `Standard`
until the order was swapped. Configure *after* the bounce, and let a change that needs
one, like clearing the cull shader, do its own.

**Batch mode has no GPU clock.** `FrameTimingManager` returns a CPU frame time and a GPU
frame time of exactly zero there, with `enableFrameTimingStats` already on in the host
project. The harness falls back to the CPU difference, says so in the run's notes, and marks
every affected row's cost basis `CPU` so a CPU figure is never quoted as a GPU one. Anything
whose claim is about GPU cost therefore cannot be closed headlessly — run it from the editor
window or a player.

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
| N1 | D | Synthetic DMX Channel Source in Ramp mode, Play, then `VRSL → URP → DMX Config → Validate DMX Channel Buffer` | PASS: every channel read back through the shader matches what was published. A constant offset in the reported channel is an indexing error; a value that looks like a neighbouring byte is a packing error |
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
| N13 | D | Profiling scene, channel source on Ramp, `universes = 4`, tick **Rotate Universes**. Let it run a second, then `VRSL → URP → DMX Config → Validate DMX Channel Buffer` and log the fixtures | PASS on every channel, and every fixture decoding the same colour it does without rotation. Judge on the decoded colour rather than on how many fixtures are lit: this is the same 50-fixture Ramp patch N9 uses, so ch 248 sits below the emit gate and reads `active=0` throughout, and a row demanding all 50 lit would fail on it for an unrelated reason. Only one universe is published per frame, so this is the row that proves the manager keeps what it was last told rather than re-uploading whatever arrived: if the flat space were rebuilt each frame, three universes in four would read 0 and the fixtures above sector 39 would flicker dark. Validating in the first few frames warns rather than fails — the source has not been round the rotation yet |
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
