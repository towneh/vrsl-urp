# VRSL URP

Unity 6 / URP 17+ realtime stage lighting and raymarched volumetric beams driven from DMX or AudioLink data. Genuine scene illumination from up to hundreds of fixtures with no per-light shadow atlas cost, authored normals from any URP-compatible shader, and no URP renderer settings touched.

## Attribution

This package is a fork of [VR Stage Lighting](https://github.com/AcChosen/VR-Stage-Lighting) (`com.acchosen.vr-stage-lighting`) by **AcChosen**, MIT-licensed. Substantial portions of the included shaders, prefabs, fixture meshes, textures, CRT decode chain, and authoring components are derived from that work — full credit and copyright to AcChosen. The URP realtime light path and the standalone restructuring on top of that base are this fork's contribution. See `LICENSE.md` and `NOTICE.md` for full attribution and a per-component breakdown.

## What this package adds

- A render-pass pipeline injected at runtime via `RenderPipelineManager.beginCameraRendering` — no URP Renderer Features required, no URP asset / renderer asset settings touched.
- Per-fixture realtime light components (`VRStageLighting_DMX_RealtimeLight`, `VRStageLighting_AudioLink_RealtimeLight`) consumed by manager singletons.
- A VRSL-owned normals prepass that renders opaque scene geometry with the standard URP `DepthNormals` / `DepthNormalsOnly` shader tags into a VRSL-owned RT, so authored surface normals from any URP-targeted shader (URP Lit, Poiyomi URP, lilToon URP, Mochie URP) come through without per-shader work.
- Half-res or full-res raymarched in-scattering for cone volumetrics with optional 3D-noise modulation and scene-fog coupling.
- URP fixture prefab variants for AudioLink and DMX (Mover Spotlight, Mover Washlight, Static Blinder, Static ParLight) that drive the fixture-body emissive directly from the realtime light component.

## Requirements

- Unity 6000.0 LTS (Unity 6) or newer
- Universal Render Pipeline 17.0+
- [AudioLink](https://github.com/llealloo/audiolink) 3.1.2+

## Coexistence with com.acchosen.vr-stage-lighting

Designed to install alongside the upstream `com.acchosen.vr-stage-lighting` package without conflict. Asset GUIDs, shader picker namespaces (`VRSL-URP/...` here vs upstream's `VRSL/...`), C# namespaces (`VRSL.URP` here vs `VRSL`), and runtime DMX globals (`_VRSLU_DMX*` here vs upstream's `_Udon_DMX*`) are all isolated so both packages can run their own pipelines in parallel without collision. Existing upstream scenes continue to work alongside; this package only adds new fixtures and rendering, never replaces upstream assets.

## License

MIT — see `LICENSE.md`.
