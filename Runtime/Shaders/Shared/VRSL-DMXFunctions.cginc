#define IF(a, b, c) lerp(b, c, step((fixed) (a), 0));

// Point/clamp sampling and the manager's grid texel size, as in VRSLDMX.hlsl, so every
// reader takes a channel from its own cell whatever the grid texture's filter.
SamplerState sampler_point_clamp;
float4 _VRSLDMXTexelSize;

// The DMX channel buffer and the compute's per-channel state, as in VRSLDMX.hlsl,
// which says how they are published and read.
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
// A universe is 520 flat channels (512 slots and 8 of padding). On a nine-universe grid,
// universes 1 to 3 are in red, 4 to 6 in green, 7 to 9 in blue: the component is the
// channel's group of three universes. 0 off nine-universe and on Legacy.
uint getDMXComponent(uint DMXChannel)
{
    return getNineUniverseMode() == 0u || _VRSLU_DMXGridLayout == 2 || DMXChannel == 0u ? 0u : (DMXChannel - 1u) / 1560u;
}

// The cell a channel occupies, in UV, as VRSLDMX.hlsl's vrsldmx_cell finds it: false for a
// channel the grid doesn't carry, channel 0, one past the last row ((s + 1) w > 13 h, or
// sector 50 on Legacy) or past universe 9. The UV is computed and sampled either way.
bool getDMXCell(uint DMXChannel, out float2 uv, out uint component)
{
    component   = getDMXComponent(DMXChannel);
    uint flat   = DMXChannel - 1560u * component;
    uint slot   = (flat - 1u) % 13u + 1u;
    uint sector = (flat - 1u) / 13u;
    bool legacy = _VRSLU_DMXGridLayout == 2;
    uv = legacy ? LegacyRead(slot - 1, sector) : IndustryRead(slot, sector + 1);
    bool onGrid = legacy ? sector < 50u : (sector + 1u) * _VRSLDMXTexelSize.z <= 13.0 * _VRSLDMXTexelSize.w;
    return DMXChannel != 0u && component <= 2u && onGrid;
}

float2 getDMXCellUV(uint DMXChannel)
{
    float2 uv;
    uint component;
    getDMXCell(DMXChannel, uv, component);
    return uv;
}

half pickDMXComponent(half4 c, uint component)
{
    return component == 0u ? c.r : (component == 1u ? c.g : c.b);
}

// A channel's value on the grid: luminance, or on a nine-universe grid its universe's
// component. A channel the grid doesn't carry reads absent.
half getValueAtCoordsOr(uint DMXChannel, Texture2D _Tex, half absent)
{
    float2 uv;
    uint component;
    bool carried = getDMXCell(DMXChannel, uv, component);
    half4 c = _Tex.SampleLevel(sampler_point_clamp, uv, 0);
    half value = getNineUniverseMode() != 0u && _VRSLU_DMXGridLayout != 2 ? pickDMXComponent(c, component)
                                                                         : LinearRgbToLuminance(c.rgb);
    return carried ? value : absent;
}

half getValueAtCoords(uint DMXChannel, Texture2D _Tex)
{
    return getValueAtCoordsOr(DMXChannel, _Tex, 0.0);
}

// A value per colour component (the spin timer): red, or on a nine-universe grid the
// channel's component. 0 where the grid has no cell.
half getValueAtCoordsRaw(uint DMXChannel, Texture2D _Tex)
{
    float2 uv;
    uint component;
    bool carried = getDMXCell(DMXChannel, uv, component);
    half value = pickDMXComponent(_Tex.SampleLevel(sampler_point_clamp, uv, 0), component);
    return carried ? value : 0.0;
}

// Every channel read goes through one of these four, as in VRSL-DMXFunctions-URP.hlsl.
half DMXValue(uint DMXChannel)
{
    if (channelBufferLive()) return loadDMXChannel(DMXChannel);
    return getValueAtCoords(DMXChannel, _VRSLU_DMXGridRenderTexture);
}

half DMXMovement(uint DMXChannel)
{
    if (channelBufferLive()) return loadDMXMovement(DMXChannel);
    return getValueAtCoords(DMXChannel, _VRSLU_DMXGridRenderTextureMovement);
}

float DMXSpinPhase(uint DMXChannel)
{
    if (channelBufferLive()) return loadDMXSpinPhase(DMXChannel);
    return getValueAtCoordsRaw(DMXChannel, _VRSLU_DMXGridSpinTimer);
}

#ifndef LASER
// 1 open, 0 closed. Below a channel value of 0.2 the fixture is held open.
half DMXStrobe(uint DMXChannel)
{
    half strobe = 1.0;
    if (!channelBufferLive())
    {
        strobe = getValueAtCoordsOr(DMXChannel, _VRSLU_DMXGridStrobeOutput, 1.0);
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
#endif

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
    
    // half phase = getValueAtCoordsRaw(DMXChannel + 6, _VRSLU_DMXGridStrobeTimer);
    // half status = getValueAtCoords(DMXChannel + 6, _VRSLU_DMXGridRenderTexture);

    half strobe = DMXStrobe(DMXChannel + 6);
    // half strobe = (sin(phase));//Get sin wave
    // strobe = IF(strobe > 0.0, 1.0, 0.0);//turn to square wave
    //strobe = saturate(strobe);

    // strobe = IF(status > 0.2, strobe, 1); //minimum channel threshold set
    
    //check if we should even be strobing at all.
    strobe = IF(isDMX() == 1, strobe, 1);
    strobe = IF(isStrobe() == 1, strobe, 1);
    
    return strobe;

}
half GetStrobeOutputFiveCH(uint DMXChannel)
{
    
    // half phase = getValueAtCoordsRaw(DMXChannel + 4, _VRSLU_DMXGridStrobeTimer);
    // half status = getValueAtCoords(DMXChannel + 4, _VRSLU_DMXGridRenderTexture);

    half strobe = DMXStrobe(DMXChannel + 4);
    // strobe = IF(strobe > 0.0, 1.0, 0.0);//turn to square wave
    //strobe = saturate(strobe);

    // strobe = IF(status > 0.2, strobe, 1); //minimum channel threshold set
    
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
half GetFinePanValue(uint DMXChannel)
{
    if (allowFineChannels() == 0)
        return 0;

    return DMXMovement(DMXChannel+1);
}
half GetPanValue(uint DMXChannel)
{
    half inputValue = DMXMovement(DMXChannel);
    inputValue = (inputValue + (GetFinePanValue(DMXChannel) / 256.));
    #if defined(VOLUMETRIC_YES) || defined(PROJECTION_YES) || defined(FIXTURE_EMIT) || defined(FIXTURE_SHADOWCAST) || defined(VRSL_SURFACE) || defined(VRSL_FLARE)
        return IF(isDMX() == 1, ((getMinMaxPan() * 2) * (inputValue)) - getMinMaxPan(), 0.0);
    #else
        return IF(isDMX() == 1, ((_MaxMinPanAngle * 2) * (inputValue)) - _MaxMinPanAngle, 0.0);
    #endif
}


half GetFineTiltValue(uint DMXChannel)
{
    if (allowFineChannels() == 0)
        return 0;

    return DMXMovement(DMXChannel+3);
}

//function for getting the Tilt Value (Channel 3)
half GetTiltValue(uint DMXChannel)
{
    half inputValue = DMXMovement(DMXChannel + 2);
    inputValue = (inputValue + (GetFineTiltValue(DMXChannel) / 256.));
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


    //return IF(isDMX() == 1,lerp(fixed4(0,0,0,1), half4(redchannel,greenchannel,bluechannel,1), GetDMXIntensity(DMXChannel, _FixtureMaxIntensity)), half4(redchannel,greenchannel,bluechannel,1) * GetDMXIntensity(DMXChannel, _FixtureMaxIntensity));
    return lerp(fixed4(0,0,0,1), half4(redchannel,greenchannel,bluechannel,1), GetDMXIntensity(DMXChannel, _FixtureMaxIntensity));
}

half getDMXConeWidth(uint DMXChannel) //Motor Speed Channel// CHANNEL 5
{
    half inputvalue = DMXValue(DMXChannel + 4);
    half DMXWidth = lerp(0, 5.5, inputvalue) - 1.5;
    return IF(isDMX() == 1, DMXWidth, getConeWidth());


}
#endif
