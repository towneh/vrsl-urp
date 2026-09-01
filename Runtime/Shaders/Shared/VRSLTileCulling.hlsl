#ifndef VRSL_TILE_CULLING_INCLUDED
#define VRSL_TILE_CULLING_INCLUDED

// Read side of the screen-space tiled light list built by VRSLLightCull.compute.
//
// Without this, both fullscreen passes loop every fixture in the scene on every
// pixel, and the volumetric pass does it once per raymarch step. The tile list
// replaces that with the handful of lights whose range actually reaches the
// tile's frustum.
//
// Buffer layout — one run of VRSL_TileStride() uints per tile:
//   [0]              fixtures that reached the tile, NOT clamped to the cap
//   [1 .. capacity]  indices into _VRSLLights
//
// Slot 0 is the honest count, so the difference between it and the cap is how
// many fixtures the tile dropped — which is the only way a scene losing
// fixtures can be told apart from one that is fine. Nothing may iterate it
// directly: past the cap there are no indices behind it, and reading them
// indexes _VRSLLights out of range. VRSL_LightListCount clamps, and it is the
// only sanctioned way to read slot 0.
//
// Tiles are ordered (eye, y, x), so single-pass instanced VR gets an
// independent list per eye.

StructuredBuffer<uint> _VRSLTileLightIndices;

// x = tiles across, y = tiles down, z = tile size in pixels,
// w = per-tile light cap.
// x == 0 means the cull pass didn't run this frame; callers fall back to the
// full light list so the scene still lights correctly, just without the saving.
//
// The cap travels in w so this file, the cull kernel and the pass that sizes
// the buffer read one value. It decides a buffer stride, and a stride the three
// of them can disagree about corrupts rather than errors.
float4 _VRSLTileParams;

uint VRSL_MaxLightsPerTile()
{
    return (uint)_VRSLTileParams.w;
}

uint VRSL_TileStride()
{
    return VRSL_MaxLightsPerTile() + 1u;   // slot 0 holds the count
}

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
//
// The clamp is load-bearing, not defensive. Slot 0 records every fixture that
// reached the tile so the diagnostics can report what was dropped; only the
// first cap of them have indices written behind them.
uint VRSL_LightListCount(uint tileIndex, uint totalLightCount)
{
    if (!VRSL_TilingActive()) return totalLightCount;
    return min(_VRSLTileLightIndices[tileIndex * VRSL_TileStride()],
               VRSL_MaxLightsPerTile());
}

// Index into _VRSLLights for the given slot of this pixel's list.
uint VRSL_LightListIndex(uint tileIndex, uint slot)
{
    if (!VRSL_TilingActive()) return slot;
    return _VRSLTileLightIndices[tileIndex * VRSL_TileStride() + 1u + slot];
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
