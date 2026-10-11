// The part of AudioLink's AudioLink.cginc the AudioLink fixture shaders read, declared here so
// they compile in a project without the AudioLink package. Copied from AudioLink 3.1.2
// (https://github.com/llealloo/audiolink, MIT); a row in the AudioLink tests checks every
// declaration against the AudioLink.cginc installed beside it.
//
// Include it before AudioLink.cginc, never after: when AudioLink's own file is already in, this
// one steps aside and AudioLink's declarations are used.
#ifndef VRSL_AUDIOLINK_DATA_INCLUDED
#define VRSL_AUDIOLINK_DATA_INCLUDED

#ifndef AUDIOLINK_CGINC_INCLUDED

    #define ALPASS_AUDIOLINK                uint2(0,0)  //Size: 128, 4
    #define ALPASS_THEME_COLOR0             uint2(0,23) //Size: 1, 1
    #define ALPASS_THEME_COLOR1             uint2(1,23) //Size: 1, 1
    #define ALPASS_THEME_COLOR2             uint2(2,23) //Size: 1, 1
    #define ALPASS_THEME_COLOR3             uint2(3,23) //Size: 1, 1
    #define ALPASS_CCLIGHTS                 uint2(0,25) //Size: 128, 2

    uniform float4               _AudioTexture_TexelSize;

    #ifdef SHADER_TARGET_SURFACE_ANALYSIS
        #define AUDIOLINK_STANDARD_INDEXING
    #endif

    #ifdef AUDIOLINK_STANDARD_INDEXING
        sampler2D _AudioTexture;
        #define AudioLinkData(xycoord) tex2Dlod(_AudioTexture, float4(uint2(xycoord) * _AudioTexture_TexelSize.xy, 0, 0))
    #else
        uniform Texture2D<float4>   _AudioTexture;
        #define AudioLinkData(xycoord) _AudioTexture[uint2(xycoord)]
    #endif

    float4 AudioLinkLerp(float2 xy) { return lerp( AudioLinkData(xy), AudioLinkData(xy+int2(1,0)), frac( xy.x ) ); }

#endif
#endif
