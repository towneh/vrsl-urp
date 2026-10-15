#ifndef VRSLDMX_INCLUDED
#define VRSLDMX_INCLUDED

// Live DMX for a URP shader of your own: a screen that takes its colour from a fixture, a
// logo that strobes with the rig. It reads what the DMX manager reads, the video grid it is
// set to or the channels a stream sends, so a shader using it stays in step with the
// package's fixtures.
//
//     #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/VRSLDMX.hlsl"
//
// Include it after URP's Core.hlsl or on its own. It declares only the manager's globals
// (every name starts _VRSLU_ or _VRSLDMX), no material properties, so a shader keeps its
// own UnityPerMaterial and stays SRP Batcher compatible.
//
// Channels are absolute, as a fixture component writes _DMXChannel: pass that straight
// through. Every read is a texel load, so all of them work in the vertex stage too.
//
//     half  VRSLDMX_Channel(uint channel)     the channel's value, 0..1, smoothed on a grid
//     half  VRSLDMX_ChannelRaw(uint channel)  the value as it arrived, before smoothing
//     half  VRSLDMX_Strobe(uint channel)      1 open, 0 closed, for that strobe channel
//     half4 VRSLDMX_Color(uint channel)       a 13-channel fixture's colour times its dimmer
//
// Functions starting vrsldmx_ (lower case) are the package's own and may change.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

// Published by the DMX manager. Unset, the textures are black and the layout is 0
// (Horizontal), so a scene with no manager reads 0 everywhere.
TEXTURE2D(_VRSLU_DMXGridRenderTexture);
TEXTURE2D(_VRSLU_DMXGridRenderTextureMovement);
TEXTURE2D(_VRSLU_DMXGridStrobeOutput);
TEXTURE2D(_VRSLU_DMXGridSpinTimer);

// The grid's size, (1/w, 1/h, w, h). Unity's automatic _TexelSize can't be trusted for a
// globally bound texture, so the manager publishes this itself.
float4 _VRSLDMXTexelSize;

// The grid layout: 0 Horizontal, 1 Vertical, 2 Legacy.
int _VRSLU_DMXGridLayout;

// The channel buffer and the compute's per-channel state, published while a channel source
// is publishing (Stream data). _VRSLU_DMXChannelCount is 0 otherwise and every read takes
// the grid, the same choice the compute makes. Channel n is byte n-1 of the channel buffer
// and element n-1 of the others. Structured buffers outside compute need shader model 5:
// below target 4.5 a shader reads the grid only.
uint   _VRSLU_DMXChannelCount;
uint   _VRSLU_StrobeStatic;
float4 _VRSLU_StrobeFreqs;
float  _VRSLU_TimeY;
int    _VRSLU_StrobeDisabled;
#if SHADER_TARGET >= 45
ByteAddressBuffer       _VRSLU_DMXChannels;
StructuredBuffer<float> _VRSLU_DMXMovement;
StructuredBuffer<float> _VRSLU_DMXSpinPhase;
StructuredBuffer<float> _VRSLU_DMXStrobePhase;
#endif

// Every grid read is point/clamp whatever filter the bound texture has: a bilinear read
// bleeds neighbouring cells into the value. Inline sampler, named so it can't clash with
// one the including shader declares.
SamplerState vrsldmx_point_clamp_sampler;

bool vrsldmx_channel_buffer_live()
{
    #if SHADER_TARGET >= 45
        return _VRSLU_DMXChannelCount > 0u;
    #else
        return false;
    #endif
}

// Index into the buffers for a channel, or -1 past what the source published, which reads
// 0 (an unlit fixture), as the compute's LoadDMXChannel does.
int vrsldmx_channel_buffer_index(uint channel)
{
    uint index = channel - 1u;
    return (channel == 0u || index >= _VRSLU_DMXChannelCount) ? -1 : (int) index;
}

float vrsldmx_load_channel(uint channel)
{
    #if SHADER_TARGET >= 45
        int index = vrsldmx_channel_buffer_index(channel);
        if (index < 0) return 0.0;
        uint word = _VRSLU_DMXChannels.Load((uint) index & ~3u);
        return float((word >> (((uint) index & 3u) * 8u)) & 0xFFu) * (1.0 / 255.0);
    #else
        return 0.0;
    #endif
}

float vrsldmx_load_movement(uint channel)
{
    #if SHADER_TARGET >= 45
        int index = vrsldmx_channel_buffer_index(channel);
        if (index < 0) return 0.0;
        return _VRSLU_DMXMovement[index];
    #else
        return 0.0;
    #endif
}

float vrsldmx_load_spin_phase(uint channel)
{
    #if SHADER_TARGET >= 45
        int index = vrsldmx_channel_buffer_index(channel);
        if (index < 0) return 0.0;
        return _VRSLU_DMXSpinPhase[index];
    #else
        return 0.0;
    #endif
}

float vrsldmx_load_strobe_phase(uint channel)
{
    #if SHADER_TARGET >= 45
        int index = vrsldmx_channel_buffer_index(channel);
        if (index < 0) return 0.0;
        return _VRSLU_DMXStrobePhase[index];
    #else
        return 0.0;
    #endif
}

float2 vrsldmx_legacy_uv(int slot, int sector)
{
    float x = 0.02000;
    float y = 0.02000;
    float ymod = floor(sector / 2.0);
    float xmod = sector % 2.0;

    x += (xmod * 0.50);
    y += (ymod * 0.04);
    y -= sector >= 23 ? 0.025 : 0.0;
    x += (slot * 0.04);
    x -= sector >= 40 ? 0.01 : 0.0;
    return float2(x, y);
}

float2 vrsldmx_industry_uv(int x, int y, float4 texelSize)
{
    float resMultiplierX = (texelSize.z / 13);
    float2 xyUV = float2(0.0, 0.0);

    xyUV.x = ((x * resMultiplierX) * texelSize.x);
    xyUV.y = (y * resMultiplierX) * texelSize.y;
    xyUV.y -= 0.001915;
    xyUV.x -= 0.015;
    return xyUV;
}

// 0 for red, 1 for green, 2 for blue.
int vrsldmx_target_color(uint universe)
{
    universe -= 1;
    return floor((int)(universe / 3));
}

// The grid cell a channel occupies, in UV: slot ((c - 1) mod 13) + 1 of sector (c - 1) div 13.
// Integers throughout, so no rounding of a division can put a channel in a neighbouring row.
// Channel 0, unpatched, sits below the grid and samples its clamped edge.
float2 vrsldmx_cell_uv(uint channel, float4 texelSize)
{
    uint universe = ceil(((int) channel) / 512.0);
    int targetColor = vrsldmx_target_color(universe);

    universe -= 1;
    channel = targetColor > 0 ? channel - (((universe - (universe % 3)) * 512)) - (targetColor * 24) : channel;

    uint slot   = channel == 0 ? 13 : (channel - 1) % 13 + 1;
    int  sector = channel == 0 ? -1 : (int)((channel - 1) / 13);
    return _VRSLU_DMXGridLayout == 2 ? vrsldmx_legacy_uv(slot - 1, sector) : vrsldmx_industry_uv(slot, sector + 1, texelSize);
}

// A channel's value in a decoded grid: the cell's luminance, or in nine-universe mode the
// colour component for its universe.
half vrsldmx_grid_value(uint channel, TEXTURE2D(tex), float4 texelSize, uint nineUniverse)
{
    uint universe = ceil(((int) channel) / 512.0);
    int targetColor = vrsldmx_target_color(universe);
    float2 xyUV = vrsldmx_cell_uv(channel, texelSize);

    half4 c = SAMPLE_TEXTURE2D_LOD(tex, vrsldmx_point_clamp_sampler, xyUV, 0);
    half value = 0.0;

    if (nineUniverse && _VRSLU_DMXGridLayout != 2)
    {
        value = c.r;
        value = lerp(c.g, value, step((float) (targetColor > 0), 0.0));
        value = lerp(c.b, value, step((float) (targetColor > 1), 0.0));
    }
    else
    {
        half3 cRGB = half3(c.r, c.g, c.b);
        value = Luminance(cRGB);
    }
    return value;
}

// A channel's value in a grid that stores one value per colour component (the spin timer).
half vrsldmx_grid_component(uint channel, TEXTURE2D(tex), float4 texelSize)
{
    uint universe = ceil(((int) channel) / 512.0);
    int targetColor = vrsldmx_target_color(universe);
    float2 xyUV = vrsldmx_cell_uv(channel, texelSize);

    float4 c = SAMPLE_TEXTURE2D_LOD(tex, vrsldmx_point_clamp_sampler, xyUV, 0);
    half value = c.r;
    value = lerp(c.g, value, step((float) (targetColor > 0), 0.0));
    value = lerp(c.b, value, step((float) (targetColor > 1), 0.0));
    return value;
}

// The reads every fixture makes, taking the channel buffer while a source publishes and the
// grid otherwise, mirroring the compute's MainChannel, MovementChannel, SpinPhase and
// StrobeValue.
half vrsldmx_value(uint channel, uint nineUniverse)
{
    if (vrsldmx_channel_buffer_live()) return vrsldmx_load_channel(channel);
    return vrsldmx_grid_value(channel, _VRSLU_DMXGridRenderTexture, _VRSLDMXTexelSize, nineUniverse);
}

half vrsldmx_value_raw(uint channel, uint nineUniverse)
{
    if (vrsldmx_channel_buffer_live()) return vrsldmx_load_channel(channel);
    return vrsldmx_grid_value(channel, _VRSLU_DMXGridRenderTexture, _VRSLDMXTexelSize, nineUniverse);
}

float vrsldmx_movement(uint channel, uint nineUniverse)
{
    if (vrsldmx_channel_buffer_live()) return vrsldmx_load_movement(channel);
    return vrsldmx_grid_value(channel, _VRSLU_DMXGridRenderTextureMovement, _VRSLDMXTexelSize, nineUniverse);
}

float vrsldmx_spin_phase(uint channel)
{
    if (vrsldmx_channel_buffer_live()) return vrsldmx_load_spin_phase(channel);
    return vrsldmx_grid_component(channel, _VRSLU_DMXGridSpinTimer, _VRSLDMXTexelSize);
}

// 1 open, 0 closed. Below a channel value of 0.2 the fixture is held open.
// One exit: with two early returns, fxc splits the grid and buffer paths into two ifs on
// the same flag and warns that the result is potentially uninitialized.
half vrsldmx_strobe(uint channel, uint nineUniverse)
{
    half strobe = 1.0;
    if (!vrsldmx_channel_buffer_live())
    {
        strobe = vrsldmx_grid_value(channel, _VRSLU_DMXGridStrobeOutput, _VRSLDMXTexelSize, nineUniverse);
    }
    else if (_VRSLU_StrobeDisabled <= 0)
    {
        float status = vrsldmx_load_channel(channel);
        float phase = 0.0;
        if (_VRSLU_StrobeStatic > 0u)
        {
            float speed = status > 0.2 ? _VRSLU_StrobeFreqs.y : _VRSLU_StrobeFreqs.x;
            speed       = status > 0.5 ? _VRSLU_StrobeFreqs.z : speed;
            phase       = _VRSLU_TimeY * speed;
        }
        else
        {
            phase = vrsldmx_load_strobe_phase(channel);
        }
        strobe = status > 0.2 ? (sin(phase) > 0.0 ? 1.0 : 0.0) : 1.0;
    }
    return strobe;
}

// The public reads.

half VRSLDMX_Channel(uint channel)
{
    return vrsldmx_value(channel, 0u);
}

// Under Stream data there is no smoothing, so this equals VRSLDMX_Channel.
half VRSLDMX_ChannelRaw(uint channel)
{
    return vrsldmx_value_raw(channel, 0u);
}

// Held open (1) below 0.2 and while strobe is switched off on the manager.
half VRSLDMX_Strobe(uint channel)
{
    return vrsldmx_strobe(channel, 0u);
}

// Channels +7, +8, +9 times the dimmer at +5, alpha 1. channel is the fixture's first.
half4 VRSLDMX_Color(uint channel)
{
    half3 rgb = half3(VRSLDMX_Channel(channel + 7u), VRSLDMX_Channel(channel + 8u), VRSLDMX_Channel(channel + 9u));
    return half4(rgb * VRSLDMX_Channel(channel + 5u), 1.0);
}

#endif
