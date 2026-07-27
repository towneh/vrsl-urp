#ifndef VRSL_TILE_CULLING_INCLUDED
#define VRSL_TILE_CULLING_INCLUDED

// Read side of the screen-space tiled light list built by VRSLLightCull.compute.
//
// Without this, both fullscreen passes loop every fixture in the scene on every
// pixel, and the volumetric pass does it once per raymarch step. The tile list
// replaces that with the handful of lights whose range actually reaches the
// tile's frustum.
//
// Buffer layout — one run of VRSL_TILE_STRIDE uints per tile:
//   [0]              light count for the tile (already clamped to the cap)
//   [1 .. count]     indices into _VRSLLights
//
// Tiles are ordered (eye, y, x), so single-pass instanced VR gets an
// independent list per eye.

// Must match VRSLLightCull.compute and VRSL_TileCulling.MaxLightsPerTile.
#define VRSL_MAX_LIGHTS_PER_TILE 64
#define VRSL_TILE_STRIDE         (VRSL_MAX_LIGHTS_PER_TILE + 1)

StructuredBuffer<uint> _VRSLTileLightIndices;

// x = tiles across, y = tiles down, z = tile size in pixels, w = unused.
// x == 0 means the cull pass didn't run this frame; callers fall back to the
// full light list so the scene still lights correctly, just without the saving.
float4 _VRSLTileParams;

bool VRSL_TilingActive()
{
    return _VRSLTileParams.x >= 1.0;
}

uint VRSL_TileIndex(float2 uv, uint eyeIndex)
{
    uint tilesX = (uint)_VRSLTileParams.x;
    uint tilesY = (uint)_VRSLTileParams.y;

    uint tx = min((uint)(saturate(uv.x) * tilesX), tilesX - 1u);
    uint ty = min((uint)(saturate(uv.y) * tilesY), tilesY - 1u);

    return (eyeIndex * tilesY + ty) * tilesX + tx;
}

// Number of lights to iterate for this pixel.
uint VRSL_LightListCount(uint tileIndex, uint totalLightCount)
{
    if (!VRSL_TilingActive()) return totalLightCount;
    return _VRSLTileLightIndices[tileIndex * VRSL_TILE_STRIDE];
}

// Index into _VRSLLights for the given slot of this pixel's list.
uint VRSL_LightListIndex(uint tileIndex, uint slot)
{
    if (!VRSL_TilingActive()) return slot;
    return _VRSLTileLightIndices[tileIndex * VRSL_TILE_STRIDE + 1u + slot];
}

// Eye slice for the tile lookup. unity_StereoEyeIndex resolves to 0 outside
// stereo rendering, so this is safe in every variant.
uint VRSL_EyeIndex()
{
#if defined(STEREO_INSTANCING_ON) || defined(STEREO_MULTIVIEW_ON)
    return unity_StereoEyeIndex;
#else
    return 0;
#endif
}

#endif // VRSL_TILE_CULLING_INCLUDED
