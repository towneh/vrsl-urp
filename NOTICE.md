# Notice

This package contains material from third parties. Each component is distributed under its own license terms. The combined package is distributed under the MIT License — see `LICENSE.md`.

## Upstream: VR Stage Lighting (AcChosen)

Substantial portions of this package are derived from [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) by AcChosen, copyright (c) 2022 AcChosen, licensed under the MIT License. Full credit and original copyright belong to AcChosen.

The following are direct derivatives of upstream — copied (with renames for coexistence) from the AcChosen package:

- All visible-mesh fixture shaders: FixtureMesh, ProjectionMesh, VolumetricMesh, LensFlare for AudioLink and DMX variants of Static (Blinder, ParLight) and Mover (Spotlight, Washlight) fixtures.
- Discoball and BasicLaser shaders (AudioLink and DMX variants).
- The `VRSL-URP/Standard Static/Surface Shaders/*` shader family (Opaque, AlphaCutout, Transparent, 12 Channel Bar).
- The DMX CRT decode chain: all five `DMXRTShader-*.shader` files plus their CustomRenderTexture assets, materials, and output `RenderTexture` assets.
- Shared HLSL includes: `VRSL-Defines-URP.hlsl`, `VRSL-DMXFunctions-URP.hlsl`, `VRSL-AudioLink-Functions-URP.hlsl`, `VRSL-LightingFunctions.cginc`, `VRSL-StandardLighting.cginc`, the per-fixture vertex / projection / volumetric `.cginc` includes.
- The fixture geometry FBX meshes (Mover Spotlight HQ, Mover Washlight HQ, ParLight, StrobeLight, etc.).
- All GOBO atlases, fixture body / projection / volumetric / lens-flare textures, mover light textures, static light textures.
- The legacy fixture authoring components: `VRStageLighting_AudioLink_Static`, `VRStageLighting_DMX_Static`, `VRStageLighting_AudioLink_Laser`, `VRSL_AudioLink_SmoothingPanel`, `VRSL_LocalUIControlPanel`, plus their associated prefabs and the GridReader OSC integration.
- The example scenes and most of their non-URP scene content (truss prefab, audio prefab, AudioLink controller prefab).

Modifications applied during the fork: shader picker names rewritten from `VRSL/...` to `VRSL-URP/...`, C# namespaces from `VRSL` to `VRSL.URP`, runtime DMX globals from `_Udon_DMX*` to `_VRSLU_DMX*`, asset GUIDs regenerated for coexistence, BIRP / VRChat / UdonSharp accommodations stripped, and asmdef names re-prefixed to `Towneh.VRSL.URP*`.

## URP-specific additions (this fork)

The following were authored by towneh on the `urp-volumetric-lights` development branch of the upstream fork prior to extraction, and constitute the URP realtime light path:

- `VRSL_URPLightManager`, `VRSL_AudioLinkURPLightManager` (manager singletons).
- `VRSLDMXLightPasses`, `VRSLAudioLinkLightPasses` (URP render pass classes).
- `VRSLNormalsPrepass` (VRSL-owned normals prepass for MSAA-agnostic authored normals).
- `VRStageLighting_{DMX,AudioLink}_RealtimeLight` (per-fixture realtime light components consumed by the managers).
- `VRSLDMXLightUpdate.compute`, `VRSLAudioLinkLightUpdate.compute` (per-fixture state decode kernels).
- `VRSLLightingLibrary.hlsl` (struct definitions and lighting evaluation helpers shared between the URP fullscreen passes).
- `Runtime/Shaders/Surface/VRSLDeferredLighting.shader`, `VRSLVolumetricLighting.shader` (the URP-only fullscreen surface lighting and raymarched in-scattering passes).
- The URP fixture prefab variants and their custom inspectors.
- The example scenes' URP variants.

## Bundled third-party plugins

### SharpOSC

`Runtime/Scripts/GridReader/SharpOSC.dll` — bundled binary plugin from SharpOSC, used by `GridReader.cs` to receive DMX-via-OSC streams. License terms in `Runtime/Scripts/GridReader/SharpOSC License.txt`.

### TekView (GridReader)

`Runtime/Scripts/GridReader/TekView.dll` — bundled binary plugin used by `GridReader.cs`. License terms in `Runtime/Scripts/GridReader/GridReader License.txt`.

## Runtime dependencies (not bundled)

The package depends on, but does not bundle, the following — these install via the Unity Package Manager and ship under their own respective licenses:

- [AudioLink](https://github.com/llealloo/audiolink) by llealloo. The URP AudioLink path consumes AudioLink's `_AudioTexture` global at runtime.
- Universal Render Pipeline (Unity).
