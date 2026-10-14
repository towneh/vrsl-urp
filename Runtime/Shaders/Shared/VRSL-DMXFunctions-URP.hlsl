#ifndef VRSL_DMX_FUNCTIONS
#define VRSL_DMX_FUNCTIONS

#ifndef VRSL_DMX
#define VRSL_DMX
#endif
#include "VRSL-Defines-URP.hlsl"

#define IF(a, b, c) lerp(b, c, step((float) (a), 0.0))

// Force point/clamp sampling for every DMX-grid read, regardless of the bound texture's
// runtime filter mode. A CustomRenderTexture's runtime filterMode frequently defaults to
// Bilinear even when the asset inspector shows Point, and the inline sampler_<Tex> picks
// that up — which bleeds neighbouring channel cells (often black) into the decode and makes
// the fixture-body surface read a different value than the render-pass light. The compute
// path samples through an explicit sampler_point_clamp; matching it here keeps both paths
// reading the exact same cell.
SamplerState sampler_point_clamp;

// Grid texel size, published by VRSL_URPLightManager as a global from the bound grid CRT's
// dimensions. Used instead of the texture's auto _TexelSize, which Unity overwrites for a
// SetGlobalTexture-bound texture — leaving the surface decode with a wrong size and the
// highest-patched fixtures sampling the wrong (black) cell. xyz/w = (1/w, 1/h, w, h).
float4 _VRSLDMXTexelSize;

// The DMX channel buffer and the compute's per-channel state, published by
// VRSL_URPLightManager while a channel source is publishing. _VRSLU_DMXChannelCount is 0
// otherwise, and every read below then takes the grid, so the choice is the manager's
// and the same one the compute makes. Channel n is byte n-1 of the channel buffer and
// element n-1 of the others. Structured buffers outside compute need shader model 5:
// below target 4.5 a shader keeps reading the grid.
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

bool channelBufferLive()
{
    #if SHADER_TARGET >= 45
        return _VRSLU_DMXChannelCount > 0u;
    #else
        return false;
    #endif
}

// Index into the buffers for a channel, or -1 past what the source published, which
// reads 0 (an unlit fixture), as the compute's LoadDMXChannel does.
int channelBufferIndex(uint DMXChannel)
{
    uint index = DMXChannel - 1u;
    return (DMXChannel == 0u || index >= _VRSLU_DMXChannelCount) ? -1 : (int) index;
}

float loadDMXChannel(uint DMXChannel)
{
    #if SHADER_TARGET >= 45
        int index = channelBufferIndex(DMXChannel);
        if (index < 0) return 0.0;
        uint word = _VRSLU_DMXChannels.Load((uint) index & ~3u);
        return float((word >> (((uint) index & 3u) * 8u)) & 0xFFu) * (1.0 / 255.0);
    #else
        return 0.0;
    #endif
}

float loadDMXMovement(uint DMXChannel)
{
    #if SHADER_TARGET >= 45
        int index = channelBufferIndex(DMXChannel);
        if (index < 0) return 0.0;
        return _VRSLU_DMXMovement[index];
    #else
        return 0.0;
    #endif
}

float loadDMXSpinPhase(uint DMXChannel)
{
    #if SHADER_TARGET >= 45
        int index = channelBufferIndex(DMXChannel);
        if (index < 0) return 0.0;
        return _VRSLU_DMXSpinPhase[index];
    #else
        return 0.0;
    #endif
}

float loadDMXStrobePhase(uint DMXChannel)
{
    #if SHADER_TARGET >= 45
        int index = channelBufferIndex(DMXChannel);
        if (index < 0) return 0.0;
        return _VRSLU_DMXStrobePhase[index];
    #else
        return 0.0;
    #endif
}

uint getDMXChannel()
{
    return (uint) round(UNITY_ACCESS_INSTANCED_PROP(Props, _DMXChannel));  
}

uint getNineUniverseMode()
{
    return (uint) UNITY_ACCESS_INSTANCED_PROP(Props, _NineUniverseMode);
}

#ifndef LASER
uint checkPanInvertY()
{
    return (uint) UNITY_ACCESS_INSTANCED_PROP(Props, _PanInvert);
}
uint checkTiltInvertZ()
{
    return (uint) UNITY_ACCESS_INSTANCED_PROP(Props, _TiltInvert);
}
#endif

float2 LegacyRead(int channel, int sector)
{
    // say we were on sector 6
    // we need to move over 2 sectors
    // and we need to move up 3 sectors

    //1 sector is every 13 channels
        float x = 0.02000;
        float y = 0.02000;
        //TRAVERSING THE Y AXIS OF THE DMX GRID
        float ymod = floor(sector / 2.0);       

        //TRAVERSING THE X AXIS OF THE DMX GRID
        float xmod = sector % 2.0;

        x+= (xmod * 0.50);
        y+= (ymod * 0.04);
        y-= sector >= 23 ? 0.025 : 0.0;
        x+= (channel * 0.04);
        x-= sector >= 40 ? 0.01 : 0.0;
        //we are now on the correct
        return float2(x,y);
}

float2 IndustryRead(int x, int y)
{
    float resMultiplierX = (_VRSLDMXTexelSize.z / 13);
    float2 xyUV = float2(0.0,0.0);
    
    xyUV.x = ((x * resMultiplierX) * _VRSLDMXTexelSize.x);
    xyUV.y = (y * resMultiplierX) * _VRSLDMXTexelSize.y;
    xyUV.y -= 0.001915;
    xyUV.x -= 0.015;
    return xyUV;
}

int getTargetRGBValue(uint universe)
{
    universe -=1;
    return floor((int)(universe / 3));
    //returns 0 for red, 1 for green, 2, for blue
}

// The grid cell a channel occupies, in UV: slot ((c - 1) mod 13) + 1 of sector (c - 1) div 13.
// Integers throughout, so no rounding of a division can put a channel in a neighbouring row.
// Channel 0, unpatched, sits below the grid and samples its clamped edge.
float2 getDMXCellUV(uint DMXChannel)
{
    uint universe = ceil(((int) DMXChannel)/512.0);
    int targetColor = getTargetRGBValue(universe);

    universe-=1;
    DMXChannel = targetColor > 0 ? DMXChannel - (((universe - (universe % 3)) * 512)) - (targetColor * 24) : DMXChannel;

    uint slot   = DMXChannel == 0 ? 13 : (DMXChannel - 1) % 13 + 1;
    int  sector = DMXChannel == 0 ? -1 : (int)((DMXChannel - 1) / 13);
    return _VRSLU_DMXGridLayout == 2 ? LegacyRead(slot - 1, sector) : IndustryRead(slot, sector + 1);
}

//function for getting the value on the DMX Grid in the bottom right corner configuration
half getValueAtCoords(uint DMXChannel, TEXTURE2D_PARAM(tex, samplerTex))
{
    uint universe = ceil(((int) DMXChannel)/512.0);
    int targetColor = getTargetRGBValue(universe);
    float2 xyUV = getDMXCellUV(DMXChannel);
        
    float4 uvcoords = float4(xyUV.x, xyUV.y, 0, 0);
    half4 c = SAMPLE_TEXTURE2D_LOD(tex, sampler_point_clamp, xyUV, 0);
    half value = 0.0;
    
    if(getNineUniverseMode() && _VRSLU_DMXGridLayout != 2)
    {
        value = c.r;
        value = IF(targetColor > 0, c.g, value);
        value = IF(targetColor > 1, c.b, value);
    }
    else
    {
        half3 cRGB = half3(c.r, c.g, c.b);
        value = Luminance(cRGB);
    }
    return value;
}

half getValueAtCoordsRaw(uint DMXChannel, TEXTURE2D_PARAM(tex, samplerTex))
{
    uint universe = ceil(((int) DMXChannel)/512.0);
    int targetColor = getTargetRGBValue(universe);
    float2 xyUV = getDMXCellUV(DMXChannel);

    float4 uvcoords = float4(xyUV.x, xyUV.y, 0,0);
    float4 c = SAMPLE_TEXTURE2D_LOD(tex, sampler_point_clamp, xyUV, 0);
    half value = c.r;
    value = IF(targetColor > 0, c.g, value);
    value = IF(targetColor > 1, c.b, value);
    return value;
}

// Every channel read goes through one of these four, which take the channel buffer
// while a source publishes and the grid otherwise, mirroring the compute's MainChannel,
// MovementChannel, SpinPhase and StrobeValue.
half DMXValue(uint DMXChannel)
{
    if (channelBufferLive()) return loadDMXChannel(DMXChannel);
    return getValueAtCoords(DMXChannel, TEXTURE2D_ARGS(_VRSLU_DMXGridRenderTexture, sampler_VRSLU_DMXGridRenderTexture));
}

float DMXMovement(uint DMXChannel)
{
    if (channelBufferLive()) return loadDMXMovement(DMXChannel);
    return getValueAtCoords(DMXChannel, TEXTURE2D_ARGS(_VRSLU_DMXGridRenderTextureMovement, sampler_VRSLU_DMXGridRenderTextureMovement));
}

float DMXSpinPhase(uint DMXChannel)
{
    if (channelBufferLive()) return loadDMXSpinPhase(DMXChannel);
    return getValueAtCoordsRaw(DMXChannel, TEXTURE2D_ARGS(_VRSLU_DMXGridSpinTimer, sampler_VRSLU_DMXGridSpinTimer));
}

// 1 open, 0 closed. Below a channel value of 0.2 the fixture is held open.
// One exit: with two early returns, fxc splits the grid and buffer paths into two ifs on
// the same flag and warns that the result is potentially uninitialized.
half DMXStrobe(uint DMXChannel)
{
    half strobe = 1.0;
    if (!channelBufferLive())
    {
        strobe = getValueAtCoords(DMXChannel, TEXTURE2D_ARGS(_VRSLU_DMXGridStrobeOutput, sampler_VRSLU_DMXGridStrobeOutput));
    }
    else if (_VRSLU_StrobeDisabled <= 0)
    {
        float status = loadDMXChannel(DMXChannel);
        float phase = 0.0;
        if (_VRSLU_StrobeStatic > 0u)
        {
            float speed = status > 0.2 ? _VRSLU_StrobeFreqs.y : _VRSLU_StrobeFreqs.x;
            speed       = status > 0.5 ? _VRSLU_StrobeFreqs.z : speed;
            phase       = _VRSLU_TimeY * speed;
        }
        else
        {
            phase = loadDMXStrobePhase(DMXChannel);
        }
        strobe = status > 0.2 ? (sin(phase) > 0.0 ? 1.0 : 0.0) : 1.0;
    }
    return strobe;
}

/////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

#if defined(VOLUMETRIC_YES) || defined(PROJECTION_YES) || defined(FIXTURE_EMIT) || defined(FIXTURE_SHADOWCAST) || defined(VRSL_SURFACE) || defined(VRSL_FLARE)
    half getMinMaxPan()
    {
        return UNITY_ACCESS_INSTANCED_PROP(Props,_MaxMinPanAngle);
    }
    half getMinMaxTilt()
    {
        return UNITY_ACCESS_INSTANCED_PROP(Props,_MaxMinTiltAngle);
    }
#endif

uint isDMX()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props,_EnableDMX);
}

uint allowFineChannels()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props,_EnableFineChannels);
}
#ifndef LASER
uint isStrobe()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props,_EnableStrobe);
}

uint instancedGOBOSelection()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props,_ProjectionSelection);
}

half getOffsetX()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props,_FixtureRotationX);
}

half getOffsetY()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props,_FixtureBaseRotationY);
}

half getStrobeFreq()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props,_StrobeFreq);
}
#endif
half4 getEmissionColor()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props,_Emission);
}
#ifndef LASER
half getConeWidth()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props,_ConeWidth) - 1.0;
}

uint isGOBOSpin()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props,_EnableSpin);
}

half getConeLength()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props, _ConeLength);
}
half getMaxConeLength(uint DMXChannel)
{
    #ifdef VOLUMETRIC_YES
    half mcl = UNITY_ACCESS_INSTANCED_PROP(Props, _MaxConeLength);
    return isDMX() == 1 && _EnableExtraChannels == 1 ? mcl + (DMXValue(DMXChannel+1) * 4) : mcl;
    #else
    return UNITY_ACCESS_INSTANCED_PROP(Props, _MaxConeLength);
    #endif
}
#endif
half getGlobalIntensity()
{
    return lerp(1.0,UNITY_ACCESS_INSTANCED_PROP(Props, _GlobalIntensity), UNITY_ACCESS_INSTANCED_PROP(Props, _GlobalIntensityBlend));
}

half getFinalIntensity()
{
    return UNITY_ACCESS_INSTANCED_PROP(Props, _FinalIntensity);
}
#ifndef LASER
half GetStrobeOutput(uint DMXChannel)
{
    half strobe = DMXStrobe(DMXChannel + 6);

    //check if we should even be strobing at all.
    strobe = IF(isDMX() == 1, strobe, 1);
    strobe = IF(isStrobe() == 1, strobe, 1);
    
    return strobe;
}

half GetStrobeOutputFiveCH(uint DMXChannel)
{
    half strobe = DMXStrobe(DMXChannel + 4);

    //check if we should even be strobing at all.
    strobe = IF(isDMX() == 1, strobe, 1);
    strobe = IF(isStrobe() == 1, strobe, 1);
    
    return strobe;
}

half getDMXGoboSelection(uint DMXChannel)
{
    half goboSelect = 30.0;

    #if defined(PROJECTION_MOVER) || defined (VOLUMETRIC_YES) 
        goboSelect = IF(UNITY_ACCESS_INSTANCED_PROP(Props, _LegacyGoboRange) > 0, 42.5, goboSelect);
    #endif

    uint value = round(((DMXValue(DMXChannel + 11))*255)/goboSelect);
    value = isDMX() > 0.0 ? value : instancedGOBOSelection();
    return clamp(value, 1, 8) -0.1;
}

half getGoboSpinSpeed (uint DMXChannel)
{
    #if defined(PROJECTION_YES) || defined(VOLUMETRIC_YES)
        half status = DMXValue(DMXChannel + 10);
        half phase = DMXSpinPhase(DMXChannel + 10);
        phase = checkPanInvertY() == 1 ? -phase : phase;
        return status > 0.5 ? -phase * 4 : phase * 4;
    #endif
    return 0.0;
}

//function for getting the Intensity Value (Channel 6)
half GetDMXIntensity(uint DMXChannel, half multiplier)
{
    return DMXValue(DMXChannel + 5) * multiplier;
}

half GetDMXChannel(uint DMXChannel)
{
    return DMXValue(DMXChannel);
}

//function for getting the Pan Value (Channel 2)
float GetFinePanValue(uint DMXChannel)
{
    if (allowFineChannels() == 0)
        return 0;

    return DMXMovement(DMXChannel+1);
}
float GetPanValue(uint DMXChannel)
{
    float inputValue = DMXMovement(DMXChannel) + GetFinePanValue(DMXChannel) / 256.0;
    #if defined(VOLUMETRIC_YES) || defined(PROJECTION_YES) || defined(FIXTURE_EMIT) || defined(FIXTURE_SHADOWCAST) || defined(VRSL_SURFACE) || defined(VRSL_FLARE)
        return IF(isDMX() == 1, ((getMinMaxPan() * 2) * (inputValue)) - getMinMaxPan(), 0.0);
    #else
        return IF(isDMX() == 1, ((_MaxMinPanAngle * 2) * (inputValue)) - _MaxMinPanAngle, 0.0);
    #endif
}

float GetFineTiltValue(uint DMXChannel)
{
    if (allowFineChannels() == 0)
        return 0;

    return DMXMovement(DMXChannel+3);
}

//function for getting the Tilt Value (Channel 3)
float GetTiltValue(uint DMXChannel)
{
    float inputValue = DMXMovement(DMXChannel + 2) + GetFineTiltValue(DMXChannel) / 256.0;
    #if defined(VOLUMETRIC_YES) || defined(PROJECTION_YES) || defined(FIXTURE_EMIT) || defined(FIXTURE_SHADOWCAST) || defined(VRSL_SURFACE) || defined(VRSL_FLARE)
        return IF(isDMX() == 1, ((getMinMaxTilt() * 2) * (inputValue)) - getMinMaxTilt(), 0.0);
    #else
        return IF(isDMX() == 1, ((_MaxMinTiltAngle * 2) * (inputValue)) - _MaxMinTiltAngle, 0.0);
    #endif
}

//Function for getting the RGB Color Value (Channels 4, 5, and 6)
half4 GetDMXColor(uint DMXChannel)
{
    half redchannel = DMXValue(DMXChannel + 7);
    half greenchannel = DMXValue(DMXChannel + 8);
    half bluechannel = DMXValue(DMXChannel + 9);

    #if defined(PROJECTION_YES)
        redchannel = redchannel * _RedMultiplier;
        bluechannel = bluechannel * _BlueMultiplier;
        greenchannel = greenchannel * _GreenMultiplier;
    #endif

    return lerp(float4(0,0,0,1), half4(redchannel,greenchannel,bluechannel,1), GetDMXIntensity(DMXChannel, _FixtureMaxIntensity));
}

half getDMXConeWidth(uint DMXChannel) //Motor Speed Channel// CHANNEL 5
{
    half inputvalue = DMXValue(DMXChannel + 4);
    half DMXWidth = lerp(0, 5.5, inputvalue) - 1.5;
    return IF(isDMX() == 1, DMXWidth, getConeWidth());
}
#endif

#endif