# Changelog

## [Unreleased]

### Fixed

- **Meshes batched by the GPU Resident Drawer no longer light as dark, fully glossy surfaces.** The drawer draws its batches with each material's own shader and ignores the override shader the surface prepass draws through, so a batched mesh landed its lit colour in the albedo capture and an opaque alpha of 1 as its smoothness: near-black and mirror-glossy under every fixture. The prepass now leaves the drawer's batches out, and they light as the neutral mid-grey surface, the same as a layer left out of `prepassLayers`. VRSL cannot read the colour, gloss or metallic of a mesh the drawer batches, since Unity offers no pass in Forward+ that exposes them; `VRSL Diagnostics` and `Validate Renderer Setup` say when the drawer is on, and that a mesh lights in its own colour again with Unity's Disallow GPU Driven Rendering component on it, or with the drawer off on the URP asset. Row S15 in the suite. Reported against rc.3.

## [0.2.0-rc.3] — 2026-09-04

### Added

- **The discoball is a realtime light.** `Discoball` on both fixture components is a point light whose dots come from a cubemap on the manager (`discoballCubemap`, the stock mirror-ball pattern on the shipped manager prefabs), looked up along the direction from the ball and turned about the fixture's up axis at `discoballSpinSpeed`. On DMX it is one channel, the dimmer, coloured by the fixture's tint; on AudioLink it follows its band. The dots land on surfaces through the lighting pass; `discoballBeams` draws them in the haze as well, off by default because it costs a cubemap fetch per raymarch step. New prefabs `VRSL-DMX-URP-Discoball-1CH` (one for every DMX mode) and `VRSL-AudioLink-Discoball-URP`; the three example scenes use them, switched on where the old projector was off. The migration pairs upstream's discoballs, Legacy mode included, with them. Rows K1 and K2 in the suite.

### Changed

- **The control panel is a local overlay on the light managers, and optional.** `VRSL_LocalUIControlPanel` finds the scene's DMX and AudioLink light managers and adjusts them for the local user: the Volumetrics buttons set the managers' quality (High, Standard, Off), the beams slider scales `volumetricIntensity`, and the slider that used to drive the projection meshes now scales the light every fixture casts. Both sliders scale what the scene was authored to rather than replacing it, and on start the panel shows the manager's own quality, so dropping the prefab into a scene changes nothing until a button is pressed. The strobe toggle reaches the manager and the strobe decode material. The body glow, lens flare, discoball and laser controls still act on materials, found by walking the scene once at start; the panel no longer carries material lists, CRT arrays or DMX mode switching, all of which the managers own. The three projection quality menus are gone from the prefab, and the prefab's buttons and sliders are wired to the panel again (they had pointed at the stripped VRChat target since the initial release).
- **The discoball's sphere is named for the discoball.** The material is `VRSL-Discoball-Sphere` and the child object in the three discoball prefabs is `Discoball-Sphere`; the GUID is unchanged, so nothing referencing the material moves.
- **The migration recognises every stock fixture.** The sibling pass pairs the laser, discoball, flasher, light bars and 6x4 strobe with their URP prefabs in Horizontal and Vertical mode on both the DMX and AudioLink sides, and the 5-channel light bar with the point bar; those fixtures keep their Static or Laser component and copy their fields by name. Legacy-mode fixtures other than the movers, blinder and par have no URP prefab, and the summary now names the ones it left where they were. The in-place pass converts the AudioLink laser as well.
- **`lightIntensity` on both light managers.** A scene-wide multiplier on the light every fixture casts, the sibling of `volumetricIntensity`. 1 leaves the scene as authored; the beams follow it since they are the same light.

### Removed

- **The projection and volumetric mesh assets.** Ten shaders, three includes, thirty materials and six textures that drew the old Built-in projection cones and volumetric beams, superseded by the realtime lighting and raymarch passes and referenced by nothing since the control panel stopped carrying material lists. A project that assigned one of those materials by hand needs the URP fixture prefab instead.

### Fixed

- **The lens flare, laser and discoball shaders compile their stereo-instanced variant.** Under single-pass instanced VR they had no render-target eye index and drew in one eye. The AudioLink laser also lacked the stereo output field on its varyings and wiped its instance id by zero-initialising after the transfer. Both DMX and AudioLink versions of all three are covered and render on desktop. The par and blinder bodies named in the rc.2 known issue were already fixed in `1746a30`, and their projection meshes are not used by the URP prefabs.

### Known issues

- **The lens flare, laser and discoball stereo fix has not been seen in a headset.** They compile with the stereo-instanced variant and render on desktop; whether both eyes draw them is unconfirmed until someone with a headset looks. Moving heads, the fixture bodies, the surface lighting and the beams are known good in both eyes.

## [0.2.0-rc.2] — 2026-09-03

### Fixed

- **The package declares `com.unity.ugui` as a dependency.** Three runtime scripts (the control panel, the AudioLink smoothing panel, the AudioLink laser) use `UnityEngine.UI`, and a project without Unity UI installed met 75 compile errors on adding the package. The Package Manager now adds Unity UI itself, as it already did for URP. Projects that have it already, which is nearly all of them, see no change. Found by installing rc.1 from its tag into an empty project.

## [0.2.0-rc.1] — 2026-09-03

The first release candidate after 0.1.0. Surfaces are lit with a real material response, beams cost what they cross rather than a fixed budget, one quality level replaces nine tuning fields, mirrors get their own policy, and there are tools that say what the package costs and whether it is working. Two changes need a look when you upgrade; they are marked **Breaking** below.

### Highlights

- **Surfaces keep their own colour and gloss when lit.** A lit floor looks like that floor, not like a white wash over it, and glossy and metal surfaces respond as they should.
- **Beams cost what they cross.** A fixture that fills little of the view now costs a fraction of one that fills all of it, and the picture is unchanged.
- **One quality control.** `quality` on the manager is `Off`, `Standard` or `High`, with fixed costs. The nine numeric tuning fields it replaces are gone.
- **Mirrors have a policy.** By default a mirror renders one quality level below the scene, so beams stay in the mirror at a lower price.
- **Tools that answer questions.** Validate Renderer Setup, Analyse This Scene, VRSL Diagnostics and the DMX Monitor say what the package needs, what it costs, whether it is working and what the desk is sending.
- **DMX as bytes.** A Basis media player showing a Truss stream feeds the rig the exact values the desk sent, with no video grid.

### Breaking

- **A DMX fixture at full output is half as bright as before.** `maxIntensity` is now on the same scale as a URP spot light's Intensity, so the dimmer curve peaks at 1 rather than at `curveMod`. Double `maxIntensity` on fixtures that looked right before, or set `curveMod` to 1.
- **The package identifier is `town.mr.vrsl-urp`**, previously `net.towneh.vrsl-urp`. Update the key in your project's `Packages/manifest.json`. Local `file:` paths and asset GUIDs are unaffected.
- **Nine tuning fields on both managers are removed** (`volumetricResolution`, `volumetricStepCount`, `volumetricUseNoise`, `volumetricNoiseScale`, `volumetricNoiseScrollSpeed`, `volumetricNoiseStrength`, `contactShadowSteps`, `contactShadowDistance`, `contactShadowThickness`), replaced by `quality`. A scene that set any of them opens at `Standard`, which is what those defaults were, and needs no migration.
- **`secondaryCameraMode` has new values**: `Match`, `Reduced`, `SurfaceOnly`, `Skip`. A scene saved with the old `Full` reads as `Match` and behaves the same. The shipped manager prefabs are at `Reduced`.

### Added

- **A real surface response.** A prepass captures each surface's own colour, smoothness and metallic through an override shader, so any URP-convention material contributes with no shader-author involvement, and the lighting pass runs URP's BRDF against it. Leave `surfacePropertiesShader` empty and every surface lights as a neutral mid-grey instead.
- **Lit surfaces layer mask** (`prepassLayers` on the manager). Layers left out still light, as plain mid-grey.
- **Screen-space contact shadows**, off by default via `contactShadowStrength`. Only from geometry the camera can see, within the level's trace distance.
- **Tiled light culling** (`lightCullShader`). Per-pixel cost follows fixtures per screen tile rather than the scene's fixture count. A tile lights with up to 256 fixtures, raised from 64, since dense rigs were silently losing three quarters of their light at the old cap.
- **The quality level** (`quality`: `Off`, `Standard`, `High`). `Standard` reproduces what the package shipped with. `Off` keeps surfaces lit and records no volumetric pass.
- **The secondary-camera policy** (`secondaryCameraMode`). `Reduced` renders a mirror one level below the scene: a scene at `High` renders mirrors at `Standard`, a scene at `Standard` at `Low`, a mirror-only level with beams at half the samples and no contact shadows. `VRSLCameraFilter.RegisterMainView` exempts a texture camera that is really the player's view, such as a stream camera.
- **The decode runs once per frame** rather than once per camera. Every camera in a frame reads one light buffer; a frame nobody renders decodes nothing.
- **`VRSL → URP → Validate Renderer Setup`.** Reports which renderer each camera uses, the depth-priming and MSAA modes, the prepass layer mask, what each camera renders at under the policy, and any opaque shader lacking `DepthOnly` and `DepthNormals` passes. Read-only.
- **`VRSL → URP → Performance`.** Analyse This Scene measures what the package costs in the open scene at each quality level and says what turning down would give back. Run Standard Sweep and Build Sweep Player measure a fixed matrix, in a built player where there is a real GPU clock, and compare it with a stored baseline. Cost is measured by difference, managers enabled against disabled over the same frames.
- **`VRSL Diagnostics`** on either manager reports shader state, decoded data, tile figures, the prepass, and what the beams took: steps per fixture, fixtures per pixel, and the share skipped as too faint to see.
- **The DMX Monitor** (`VRSL → URP → DMX Config → DMX Monitor`). Every channel of a universe live, which DMX path the fixtures read, and how long ago each universe was heard from. Read-only.
- **DMX as bytes.** `BasisUserDataToVRSLDMX` takes Truss records from a `BasisMediaPlayer` as each frame is shown and hands the values to the manager, with a CRC check per record. `VRSL → URP → DMX Config → Add Basis DMX Record Source (SEI)` sets it up. Any component implementing `IVRSLDMXChannelSource` can do the same.
- **DMX over video from a Basis media player.** `BasisVideoToVRSLDMX` and `BasisVideoRenderTextureOutput` feed the grid decode from the player's output, including a grid that occupies part of a larger frame. `Add Basis DMX Video Output (Horizontal)` sets the transpose horizontal mode needs. Both ship in their own assembly and compile only when `com.basis.mediaplayer` is present.
- **Self-wiring managers.** The manager resolves its shader and texture references itself, shows one status line saying whether it is wired, and `VRSL → URP → Repair Manager Wiring` fills anything missing. Four references are optional and their tooltips say so.
- `DMXFixtureType.StaticPointLight`, a fixture type that lights in every direction, and `use5ChannelMode` for fixtures patched on the five-channel static layout.
- The manager publishes the DMX grid textures for the fixture-body shaders itself, so `VRSL_LocalUIControlPanel` is no longer needed on the URP path, and `dmxStrobeTimerTexture` supplies the strobe timer the strobe decode needs.

### Changed

- **Beams cost what they cross.** The step count follows the length of the cone along the view ray at a fixed spacing (0.35 m at `Standard`, 0.20 m at `High`), never below four steps and never above the level's ceiling. A fixture that cannot reach a pixel is skipped before any stepping.
- **VRSL reads URP's normals where URP already draws them**, saving an opaque pass per camera on a depth-primed renderer at MSAA 1. Elsewhere it draws its own, as before, and `forceOwnNormals` under Troubleshooting forces that everywhere.
- The haze in the beams is a baked texture rather than a function evaluated per sample. Grain may differ from before; structure does not.
- Volumetric cones no longer show structured stepping: each light is marched only across the part of the view ray inside its cone, the half-resolution upsample reconstructs gradients correctly, and the step dither no longer streaks along diagonals.
- The full-resolution volumetric mode is gone. Half resolution with a bilateral upsample is the only path, and `High` spends its budget on steps rather than pixels.
- The surface prepass runs once per camera whichever managers are present, and not at all while a manager has no fixtures.
- Surfaces hidden by a shader's own visibility logic (an alpha clip, a vertex discard) no longer pick up light through the wrong albedo.
- `VRSLLightData` is 64 bytes rather than 80. Shaders read the packed fields through `VRSL_LightType`, `VRSL_GoboIndex` and `VRSL_IsActive`.
- `VRSLNormalsPrepass` is now `VRSLSurfacePrepass`.
- The gobo wheel is packed on the GPU, at the resolution of the largest source rather than resampled to 256².
- The AudioLink moving-head shaders declare their own depth passes rather than borrowing URP Lit's.
- The three example scenes ship with their non-URP fixtures (flashers, lasers, light bars, strobes, discoballs) switched off until each is ported.
- The DMX screen-reader prefabs no longer carry a `MeshCollider`, and the control panel prefab is stripped of two leftover UdonSharp behaviours.

### Removed

- `strobeLowFrequency`, which could never reach the output.
- The `Directional Light (For Depth)` prefab and its instances, and the `RequireDepthLight` control. Depth comes from the pipeline; no scene light of any kind is needed. **If you copied that prefab into your own scene, delete it.**

### Fixed

- DMX channels past the second in each grid row decoded from the row below.
- Fixture-body surfaces decoded a black grid while the render passes were active.
- A fixture whose colour faded to black with its dimmer up kept emitting.
- Point-light fixtures were masked to near-zero whenever the manager had gobo textures assigned.
- A light manager switched off in the inspector took the singleton and stopped the running one from working. A manager switched off and on again now keeps working, on both paths.
- The three DMX manager prefabs shipped without their strobe timer texture, which could take a strobing rig dark.
- With one manager set to skip secondary cameras and the other set to render them, nobody drew the surface prepass on those cameras.
- `BasisVideoRenderTextureOutput` gamma-encoded the DMX values it framed, so every channel arrived too low.
- The control panel inspector threw before drawing its fields when `VERSION.txt` was missing; the package ships it now.
- The VRSL logo is back at the top of the inspectors.

### Known issues

- **Par, blinder, laser and discoball fixture bodies do not render correctly under single-pass instanced VR.** The body or projection geometry is missing or misplaced in one or both eyes. Moving heads, surface lighting and beams are unaffected.

## [0.1.0] — Initial release

### URP Realtime Lights — Unity 6 / URP 17+

The pipeline is four Render Graph passes, all reading the same per-fixture GPU buffer, injected at runtime by the manager MonoBehaviour via `RenderPipelineManager.beginCameraRendering`. No URP Renderer Features required, no URP asset / renderer asset settings touched by the package; co-exists with whatever rendering path, MSAA setting, depth-priming mode, and depth-texture configuration the project uses.

A VRSL-owned normals prepass renders opaque scene geometry with the standard URP `DepthNormals` / `DepthNormalsOnly` shader tags into a VRSL-owned RT, so authored surface normals from any URP-targeted shader (URP Lit, Poiyomi URP, lilToon URP, Mochie URP) come through automatically — avatars and props receive smooth-shaded VRSL light without their authors needing to add anything URP-specific.

- **Compute** decodes per-fixture state (position, direction, colour, intensity, cone, gobo) into a `StructuredBuffer`.
- **DMX** — `VRStageLighting_DMX_RealtimeLight` + `VRSL_URPLightManager`. Decodes the existing CRT chain on the GPU; no per-frame CPU cost per fixture.
- **AudioLink** — `VRStageLighting_AudioLink_RealtimeLight` + `VRSL_AudioLinkURPLightManager`. Reads animated transform directions on the CPU each frame and samples the global `_AudioTexture` on the GPU. Per-fixture color modes include emission, theme colors, ColorChord, and ColorTexture sampling against an optional scene-wide `samplingTexture` on the manager.

URP fixture prefab variants ship as standalone — the Realtime light is the sole authoring surface and drives the fixture body emissive directly via `fixtureShellRenderers`. Volumetric controls (resolution mode, modulated 3D-noise density, scene-fog coupling) and per-fixture controls (`emitterDepth`, `globalIntensity`, `targetToFollow`, `lensTransform`) are exposed on the manager and Realtime light inspectors. See the [Architecture](https://github.com/towneh/vrsl-urp/wiki/Architecture) page of the wiki and the [Fixture Configuration Reference](https://github.com/towneh/vrsl-urp/wiki/Fixture-Configuration-Reference) on the wiki for details.

Editor menu utilities:
- **VRSL → URP → Add Light Manager to Active Scene** drops a configured manager into the active scene with compute / lighting / volumetric shader references assigned.
- **VRSL → URP → AudioLink Config → Setup AudioLink Realtime Lights in Scene** configures every AudioLink mover spotlight in one click.

### Package origin

Extracted from the `urp-volumetric-lights` development branch on towneh's fork of `com.acchosen.vr-stage-lighting`, then refactored as a coexisting URP overlay package so the URP path can ship and evolve independently of the upstream VRChat-targeted package.

### Coexistence with com.acchosen.vr-stage-lighting

Standalone — no dependency on `com.acchosen.vr-stage-lighting`. The fork carries its own copies of the CRT DMX decode chain, fixture meshes and shaders under distinct asset GUIDs, shader namespaces (`VRSL-URP/…`), C# namespaces (`VRSL.URP`) and runtime globals (`_VRSLU_DMX*`), so it installs alongside the upstream Built-in-pipeline package without collision. That coexistence exists to make BIRP → URP migration incremental: both packages can run their own fixtures in the same project while a scene is moved across. Existing upstream scenes and prefabs continue to work alongside; this package never replaces upstream assets.
