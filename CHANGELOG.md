# Changelog

## [Unreleased]

### Changed

- **The mover body shaders have no Lighting Model or Light Probe Sampling setting.** Both lighting models now light through URP, so the two settings no longer choose anything. The body takes its smoothness from the metallic map's alpha times Smoothness, as the Legacy model did, so a material that was on GGX looks a little less glossy where its map's alpha is below 1.

- **Lens flares have no "Toggle The Requirement of the depth light" setting.** It never reached the shader, and flares now always hide behind what blocks their light.

- **`SmoothingPanelOpener` and `VRSL_AudioLink_SmoothingPanel` are in the `VRSL.URP` namespace,** like the rest of the package. In the global namespace they could clash with a type of the same name in another package or the project. Scenes and prefabs that use them are unaffected; code that names either one needs `using VRSL.URP;`.

### Removed

- **The `VRSL-URP/Other/Standard` shader.** It was a copy of URP's Lit shader, so the package's five materials that used it (the discoball sphere, the example truss and the three AudioLink controller materials) now use URP Lit itself and look the same. A material of your own on it shows pink: switch it to `Universal Render Pipeline/Lit`, which takes the same properties.

### Fixed

- **Moving-head bodies are lit by URP.** The spot and wash mover body shaders, DMX and AudioLink, lit themselves through the Built-in Render Pipeline's light variables, which URP never sets, so they took no main light, no additional lights and no shadows. They now light as URP Lit does, ambient from light probes or probe volumes included, and cast the shadow of what they draw, in the pose they're drawn in.

- **AudioLink par, blinder, flasher and light bar bodies render lit.** Their shader drew them only while URP rendered main-light shadows with a single cascade, so with more than one (Basis uses four) they rendered black, emission included. On a Forward+ renderer they also lost the main light, because the shader wasn't built with URP's cluster light loop.
- **DMX par, blinder and light bar bodies take the main light and ambient light.** Their shader wasn't built with URP's cluster light loop, so on a Forward+ renderer they lost the main light, and on any renderer it gave them no ambient or reflected light.
- **Surfaces keep their colour under VRSL light at any render scale.** The surface capture looked up the camera's depth by the unscaled screen size, so at a render scale other than 100% it rejected most surfaces and they lit as neutral grey.
- **Fixture bodies write world-space normals in their depth-normals pass.** The par, blinder, flasher and light bar body shaders, DMX and AudioLink, wrote view-space normals packed into 0..1, where URP and VRSL both read signed world-space normals. Effects that read the camera's normals texture, such as SSAO, and VRSL light falling on the bodies saw them facing the wrong way.
- **Fixture body materials with no normal map light as flat surfaces.** The same body shaders defaulted their normal map to white, which unpacks to a tilted normal, so a material with nothing assigned lit and wrote normals as if every surface faced off to one side. They now default to a flat normal map.
- **Moving-head bodies write their normals in their depth-normals pass.** The spot and wash mover body shaders, DMX and AudioLink, wrote their lit colour where URP and VRSL read a world-space normal, so SSAO and VRSL light falling on the bodies used garbage normals. Their depth passes also ran the full lighting for nothing; they now keep only the body's discards.
- **The AudioLink wash mover body shader has its own name,** `VRSL-URP/AudioLink/Wash Mover/Fixture`. It was named `VRSL-URP/AudioLink/Standard Mover/Fixture`, the same as the AudioLink spot mover, so `Shader.Find` on that name could return either. Materials refer to their shader by asset, so they're unaffected; code that looks the wash mover up by name needs the new one.
- **AudioLink lens flares keep their colour in fog.** With scene fog on, the AudioLink blinder and flasher flares drew as a disc in the fog's colour instead of the light's, at the same brightness whatever the light's colour and level. Lens flares are now left unfogged, as the DMX ones already were.
- **Lens flares on a new material hide behind what blocks their light.** Only the shipped flare materials did; a flare material made from either lens flare shader drew through walls.
- **Shaders no longer name custom material inspectors the package doesn't include.** Their materials show Unity's standard inspector, as they already did.
- **DMX par, blinder and light bar bodies take point and spot lights on a Forward renderer.** Their shader had no additional-light variants, so on plain Forward, the renderer Basis uses on Quest, scene point and spot lights didn't reach them, per pixel or per vertex. Forward+ already lit them.
- **Double-sided surfaces keep their colour under VRSL light from both sides.** The surface capture drew front faces only, so the back of a material with culling off (a sheet, a banner, a leaf card) lit as neutral grey. It now captures both faces and still leaves out any face the material's own shader culls.
- **The DMX laser and lens flares read each channel from its own cell of a grid with any filter.** They sampled the video grid through its texture's own filter, so on a Grid Textures asset filled with render textures left at Unity's default Bilinear filter they blended each channel with its neighbours. They now read the grid point by point, sized by the DMX manager, as the fixture bodies and realtime lights already did. The shipped grid textures are Point, so the package's own grids read the same as before.
- **DMX par, blinder and light bar bodies take smoothness from the metallic map's alpha,** as the map's label says. They read it from the red channel, the metallic one, so a material with a metallic map but a black red channel rendered fully rough, and one with a bright red channel rendered glossier than its alpha. A material with no map looks the same as before.

## [0.3.0-rc.4] — 2026-10-12

### Changed

- **The video output's Rotate 90° button is now two:** **Turn 90° Anticlockwise** and **Turn 90° Clockwise** (the old button turned clockwise). A horizontal grid strip goes in anticlockwise.

### Fixed

- **Horizontal grids from video read each fixture's channels in order.** `Add Basis DMX Video Output (Horizontal)` framed the grid reflected about its diagonal, so it read channel 1 from the strip's bottom row where a show puts it on the top, and every fixture's 13 channels came out backwards. It now frames the grid the way VRSL's own reader camera does, across the bottom 208 rows of a 1080p picture. Run the menu item again on an output it set up; for corners dragged by hand, press **Flip Horizontal** once.
- **Bake Framing From Camera works with any flat screen.** It assumed a one-unit quad, so a bake from VRSL's stock DMX screen (two units across, mirrored) gave the wrong corners. It now reads the screen mesh's own UVs, and carries on past the screen's edges when the camera frames wider than it.

## [0.3.0-rc.3] — 2026-10-11

### Changed

- **AudioLink is optional.** The package no longer depends on `com.llealloo.audiolink`, so a DMX-only project, or a Basis project with AudioLink removed, installs it without AudioLink and the DMX side works as before. Install AudioLink 3.1.2 or newer to use the AudioLink fixtures, their light manager, the AudioLink menus and the AudioLink example scene; scenes and prefabs that use them reconnect when it's installed.
- **The AudioLink components moved to their own assemblies,** `Towneh.VRSL.URP.AudioLink` and `Towneh.VRSL.URP.AudioLink.Editor`, which compile only where AudioLink is installed. Their names and the `VRSL.URP` namespace are unchanged. If an assembly definition of your own uses the AudioLink manager or fixtures, add a reference to `Towneh.VRSL.URP.AudioLink`.
- **The control panel adjusts every VRSL light manager in the scene.** Its AudioLink Manager field is gone; the panel already found the manager itself when the field was empty, which is how the shipped panel used it.
- **The AudioLink example scene uses AudioLink's own AudioLink and controller prefabs,** set up as before, with the VRSL smoothing panel as part of the scene.

### Removed

- **`AudioLink-VRSLEdition` and `AudioLinkController-WithVRSLSmoothing`.** Both wrapped AudioLink's own prefabs, so they failed to import in a project without AudioLink. A scene of your own that placed one shows a missing prefab: use AudioLink's prefabs as the AudioLink example scene does.

### Fixed

- **AudioLink theme colours on fixture bodies.** A realtime light set to a theme colour gave its body the wrong one: ThemeColor0 drew a black body, and the others showed the colour before theirs. The cast light was already right.
- **AudioLink texture colour modes with no sampling texture** now read AudioLink's own atlas, as the manager says they do. They read a black stand-in, so a fixture lit flat white or black.
- **Removing an AudioLink fixture while playing** no longer stops every AudioLink light until `RefreshFixtures()` is called; the removed fixture goes dark until then.
- **An AudioLink static fixture or laser with no renderers** says so in the Console instead of throwing.
- **The AudioLink smoothing panel** shows whole percentages.

## [0.3.0-rc.2] — 2026-10-11

### Changed

- **One set of prefabs.** Every DMX fixture and the DMX manager sit together in `Runtime/Prefabs/DMX/`, the AudioLink ones in `Runtime/Prefabs/AudioLink/`, and the names lose `-H-` and `-URP` (`VRSL-DMX-Mover-Spotlight-13CH`, `VRSL-AudioLink-Discoball`). Each fixture prefab serves every grid layout and the one DMX manager takes the layout from its DMX Input. The prefabs keep their GUIDs, so scenes that use them are unaffected.
- **The per-layout prefabs are superseded.** The Legacy and Vertical fixtures and managers moved to `Runtime/Prefabs/DMX/Superseded/`. **VRSL → URP → Replace Superseded Prefabs in Scene** moves a scene onto the single prefabs, keeping what was set on each instance and each manager's DMX Input. They'll be removed in a later release.
- **The profiling sample builds the realtime-light scene only.** Its mesh-shader option needed fixture prefabs and a depth-light prefab the package no longer ships. Re-import the sample to pick it up.
- **The migration tool brings the Horizontal and Legacy example surfaces across** onto the example surface prefab, which serves every layout like the rest.
- **Migrated fixtures glow like fresh ones.** The migration tool scales a mover's, par's, blinder's or discoball's tint by the two prefabs' defaults instead of copying it as it is. The original package's fixtures mostly default to a tint of 2 and this package's movers and discoballs to 1, so a fixture migrated with 0.3.0-rc.1 or earlier can glow twice as bright as one placed fresh.

### Removed

- **The shader-driven discoballs** (`VRSL-DMX-Static-DiscoBall-H-1CH`, `-V-1CH` and `VRSL-AudioLink-DiscoBall`) with their materials and shaders. The realtime discoball replaces them, and the migration tool already paired upstream's discoballs with it. The control panel's discoball quality buttons only changed those, so they're gone too; its discoball intensity slider still drives the realtime discoballs.
- **The Vertical DMX example scene.** The Horizontal one shows the same rig; set its manager's DMX Input for another layout.

### Fixed

- **Migrating a VRSL v2.3.0 world keeps each AudioLink fixture's band.** v2.3.0 stores it as a number, and every migrated AudioLink fixture ended on Bass. A setting the tool can't carry across now shows a warning in the Console.
- **No more missing scripts from the shipped prefabs.** Seventeen fixture prefabs and the AudioLink smoothing controller carried VRChat components that don't exist outside VRChat, so every instance arrived with a missing script. The smoothing controller's sliders and reset button now call its smoothing panel directly; they were wired to it through one of those components, so they changed nothing.

## [0.3.0-rc.1] — 2026-10-11

### Added

- **One DMX manager for any grid, or for stream data.** **DMX Input** on the manager chooses where the lights read DMX: the **Horizontal**, **Vertical** or **Legacy grid** in the video, or **Stream data** from a channel source (Truss records from a Basis stream, or the Truss OSC source). Every fixture takes the layout from the manager, so one prefab works on any layout. A world can switch while it runs with `SetDmxInput` (0 to 3, ready for a UI Dropdown), and keeps the choice the same on every client with a networked script of its own; the wiki has an example.
- **Every fixture follows stream data.** Flashers, light bars, strobes, lasers, lens flares, the discoball, the mover heads and the par and blinder bodies read the channel source with the realtime lights, with the same movement smoothing, strobe and gobo spin. Under a grid input a source waits on standby.
- **Legacy grids and fixtures.** Realtime lights decode a Legacy grid, and the migration tool brings Legacy and Vertical fixtures, both of upstream's Legacy prefab sets included, onto the Horizontal prefabs.
- **`ApplyAddress()` re-patches a DMX fixture while the world runs,** beam and body together.
- **The Basis video output can follow the manager's grid** (**Follow Manager Layout**), with a corner set per layout.

### Changed

Upgrading from 0.2.0-rc.6:

- **Unity 6000.4 and URP 17.4 or newer.** The fixture shaders that read DMX need shader model 4.5.
- **Open and save each scene with a DMX manager once.** Its five decode texture fields become DMX Input and a **Grid Textures** asset, and a manager beside an enabled channel source comes up on Stream data, since a source is now read only under Stream data. Re-import the profiling sample.
- **Fixtures take the grid layout from the manager.** A material's `_EnableCompatibilityMode` no longer does anything, and `Runtime/Shaders/VRSLDMX.cginc` is gone: a custom shader that included it should use the shared DMX includes in `Runtime/Shaders/Shared/` instead.
- **Some fixtures look different.** Par and blinder bodies and blinder flares now follow DMX instead of glowing at full (**Shell Emission Tint** sets their brightness), and migrated Legacy and Vertical fixtures carry the Horizontal prefabs' v2.4 materials.

### Fixed

- **Decoding:** strobe from a video grid, Vertical and Legacy managers' strobe timing, the 13th channel of some sectors read from the wrong grid cell, and 5-channel pars and blinders casting light from the wrong channels.
- **Fixtures:** mover heads now turn with their beams under a channel source and draw their depth in the same pose as their colour, and the Vertical flasher glows only at its lens.
- **Editor:** Play mode no longer saves changes into the package's decode textures, and the control panel's strobe toggle no longer edits the package's materials.

## [0.2.0-rc.6] — 2026-10-05

### Added

- **Truss OSC DMX source.** Lights the rig straight from `truss-relay --osc`, so you can check what a desk is sending in the Editor with no encoder, ingest or player running. Add it from **VRSL → URP → DMX Config → Add Truss OSC DMX Source**.
  - It reads the same records the stream carries, so the fixtures show what viewers would see.
  - Only Truss's `/truss/dmx` message is read. Anything else on the port is counted as ignored and can't light a fixture or stop the source.
  - In Play mode the inspector shows whether it's listening, what has arrived, and what to check when nothing lights. A port already in use is reported, not thrown.
- **`VRSLTrussRecordSource`**, a base class in the core assembly for any source of Truss records: hand it a record's bytes on the main thread and it decodes them and feeds the light manager. The Basis SEI source now builds on it and behaves as before.

### Fixed

- **The Truss SEI DMX source works with Basis's media player.** It listened for `UserDataReceived`, but the player raises `OnUserDataReceived`, so the source warned that no stream data was available and the fixtures stayed dark.

## [0.2.0-rc.5] — 2026-09-07

### Fixed

- **The package compiles against the stock Basis media player.** The Truss SEI DMX source subscribed to the player's `UserDataReceived` event at compile time, and the media player Basis ships does not have one, so every project with `com.basis.mediaplayer` failed to compile the package's Basis integration on install. The source now binds the event by name at runtime: where the player raises it, records arrive as before; where it does not, the source logs one warning saying the player cannot feed it and stays idle. The video-to-DMX source is unaffected either way.

## [0.2.0-rc.4] — 2026-09-04

### Added

- **The surface prepass reads metallic-smoothness maps.** Where a material uses one, metallic comes from the map's red channel and smoothness from its alpha scaled by the material's smoothness value, the way URP Lit and the Standard shader read theirs, so a floor whose gloss varies across a texture lights that way under the fixtures instead of as one flat value. The map keyword decides, as it does in the material's own shader: with the keyword off the scalars are read even if a map is assigned. Smoothness from the base map's alpha is honoured too.

### Fixed

- **Meshes batched by the GPU Resident Drawer no longer light as dark, fully glossy surfaces.** The drawer draws its batches with each material's own shader and ignores the override shader the surface prepass draws through, so a batched mesh landed its lit colour in the albedo capture and an opaque alpha of 1 as its smoothness: near-black and mirror-glossy under every fixture. The prepass now leaves the drawer's batches out, and they light as the neutral mid-grey surface, the same as a layer left out of `prepassLayers`. VRSL cannot read the colour, gloss or metallic of a mesh the drawer batches, since Unity offers no pass in Forward+ that exposes them; `VRSL Diagnostics` and `Validate Renderer Setup` say when the drawer is on, and that a mesh lights in its own colour again with Unity's Disallow GPU Driven Rendering component on it, or with the drawer off on the URP asset. Reported against rc.3.

## [0.2.0-rc.3] — 2026-09-04

### Added

- **The discoball is a realtime light.** `Discoball` on both fixture components is a point light whose dots come from a cubemap on the manager (`discoballCubemap`, the stock mirror-ball pattern on the shipped manager prefabs), looked up along the direction from the ball and turned about the fixture's up axis at `discoballSpinSpeed`. On DMX it is one channel, the dimmer, coloured by the fixture's tint; on AudioLink it follows its band. The dots land on surfaces through the lighting pass; `discoballBeams` draws them in the haze as well, off by default because it costs a cubemap fetch per raymarch step. New prefabs `VRSL-DMX-URP-Discoball-1CH` (one for every DMX mode) and `VRSL-AudioLink-Discoball-URP`; the three example scenes use them, switched on where the old projector was off. The migration pairs upstream's discoballs, Legacy mode included, with them.

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
