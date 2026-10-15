#ifndef VRSL_DMX_FUNCTIONS
#define VRSL_DMX_FUNCTIONS

#ifndef VRSL_DMX
#define VRSL_DMX
#endif
#include "../VRSLDMX.hlsl"
#include "VRSL-Defines-URP.hlsl"

#define IF(a, b, c) lerp(b, c, step((float) (a), 0.0))

// The package's fixtures read DMX through VRSLDMX.hlsl like any other shader; these
// wrappers add the per-fixture nine-universe flag from the Props buffer.

bool channelBufferLive()
{
    return vrsldmx_channel_buffer_live();
}

float loadDMXChannel(uint DMXChannel)
{
    return vrsldmx_load_channel(DMXChannel);
}

float loadDMXMovement(uint DMXChannel)
{
    return vrsldmx_load_movement(DMXChannel);
}

float loadDMXSpinPhase(uint DMXChannel)
{
    return vrsldmx_load_spin_phase(DMXChannel);
}

float loadDMXStrobePhase(uint DMXChannel)
{
    return vrsldmx_load_strobe_phase(DMXChannel);
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

float2 getDMXCellUV(uint DMXChannel)
{
    return vrsldmx_cell_uv(DMXChannel, _VRSLDMXTexelSize);
}

half getValueAtCoords(uint DMXChannel, TEXTURE2D(tex))
{
    return vrsldmx_grid_value(DMXChannel, tex, _VRSLDMXTexelSize, getNineUniverseMode());
}

half getValueAtCoordsRaw(uint DMXChannel, TEXTURE2D(tex))
{
    return vrsldmx_grid_component(DMXChannel, tex, _VRSLDMXTexelSize);
}

// Every channel read goes through one of these four, which take the channel buffer
// while a source publishes and the grid otherwise.
half DMXValue(uint DMXChannel)
{
    return vrsldmx_value(DMXChannel, getNineUniverseMode());
}

float DMXMovement(uint DMXChannel)
{
    return vrsldmx_movement(DMXChannel, getNineUniverseMode());
}

float DMXSpinPhase(uint DMXChannel)
{
    return vrsldmx_spin_phase(DMXChannel);
}

// 1 open, 0 closed. Below a channel value of 0.2 the fixture is held open.
half DMXStrobe(uint DMXChannel)
{
    return vrsldmx_strobe(DMXChannel, getNineUniverseMode());
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