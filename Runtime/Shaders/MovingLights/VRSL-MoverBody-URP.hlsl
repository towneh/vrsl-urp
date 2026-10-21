#ifndef VRSL_MOVER_BODY_URP
#define VRSL_MOVER_BODY_URP

// The body of a moving head, lit by URP. A pass defines VRSL_DMX or VRSL_AUDIOLINK (and WASH
// for a wash) and VRSL_MOVER_FORWARD for the lit pass or VRSL_MOVER_SHADOW for the shadow
// caster. Every pass poses the mesh through VRSLMoverPose and discards what the body doesn't
// draw, so depth priming, the normals and the shadow all follow the body as it moves.

#define FIXTURE_EMIT

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#if defined(VRSL_AUDIOLINK)
    #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/AudioLink/Shared/VRSL-AudioLink-Functions-URP.hlsl"
#else
    #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSL-DMXFunctions-URP.hlsl"
#endif
#if defined(VRSL_MOVER_FORWARD) && defined(_VRSL_LTCGI)
    #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSL-LTCGI-URP.hlsl"
#endif

TEXTURE2D(_BumpMap);
SAMPLER(sampler_BumpMap);
TEXTURE2D(_OcclusionMap);
SAMPLER(sampler_OcclusionMap);
half _OcclusionStrength;

struct VRSLMoverAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 uv         : TEXCOORD0;
    float2 uv1        : TEXCOORD1;
    float4 color      : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VRSLMoverVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float2 uv1        : TEXCOORD1;
    float3 positionWS : TEXCOORD2;
    float3 normalWS   : TEXCOORD3;
    float4 tangentWS  : TEXCOORD4;
    float4 color      : COLOR;
#if defined(VRSL_MOVER_FORWARD)
    half4 fogFactorAndVertexLight : TEXCOORD5;
    #if defined(VRSL_DMX)
        float2 intensityStrobe : TEXCOORD6;
        float4 rgbColor        : TEXCOORD7;
    #endif
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// Vertices coloured red with green stretch about _ProjectionRangeOrigin by _ProjectionRange.
float3 VRSLMoverProjectionScale(float3 positionOS, float4 color)
{
    float3 origin = _ProjectionRangeOrigin.xyz;
    return color.r == 1.0 && ceil(color.g) == 1.0 ? (positionOS - origin) * _ProjectionRange + origin : positionOS;
}

#if defined(VRSL_DMX)
// Green vertices (the yoke) pan; blue vertices (the head) pan and tilt about
// _FixtureRotationOrigin. w is 1 for a position and 0 for a direction.
float3 VRSLMoverRotate(float4 v, float4 color, float pan, float tilt)
{
    float s, c;
    sincos(radians(getOffsetY() + pan), s, c);
    float3x3 panMatrix = float3x3(c, -s, 0, s, c, 0, 0, 0, 1);
    if (checkPanInvertY() == 1)
        panMatrix = transpose(panMatrix);

    sincos(radians(getOffsetX() + tilt), s, c);
    float3x3 tiltMatrix = float3x3(1, 0, 0, 0, c, -s, 0, s, c);
    if (checkTiltInvertZ() == 1)
        tiltMatrix = transpose(tiltMatrix);

    float3 origin = v.w * _FixtureRotationOrigin.xyz;
    if (color.g == 1.0)
        return mul(panMatrix, v.xyz);
    if (color.b == 1.0)
        return mul(mul(panMatrix, tiltMatrix), v.xyz - origin) + origin;
    return v.xyz;
}
#endif

void VRSLMoverPose(inout VRSLMoverAttributes v)
{
    v.positionOS.xyz = VRSLMoverProjectionScale(v.positionOS.xyz, v.color);
    #if defined(VRSL_DMX)
        uint dmx = getDMXChannel();
        float pan = GetPanValue(dmx);
        float tilt = GetTiltValue(dmx);
        v.positionOS.xyz = VRSLMoverRotate(float4(v.positionOS.xyz, 1.0), v.color, pan, tilt);
        v.normalOS = VRSLMoverRotate(float4(v.normalOS, 0.0), v.color, pan, tilt);
        v.tangentOS.xyz = VRSLMoverRotate(float4(v.tangentOS.xyz, 0.0), v.color, pan, tilt);
    #endif
}

// The parts of the fixture mesh the body doesn't draw: vertices coloured red with blue, and
// UV (0.9, 0.9).
bool VRSLMoverBodyDiscards(VRSLMoverVaryings i)
{
    return (i.uv.x == 0.9 && i.uv.y == 0.9) || (ceil(i.color.r) != 0.0 && ceil(i.color.b) != 0.0);
}

float3 VRSLMoverBodyNormal(VRSLMoverVaryings i)
{
    half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, i.uv), _BumpScale);
    // Unnormalised basis, as in URP Lit: a mesh without tangents falls back to its vertex normal.
    float3 bitangentWS = i.tangentWS.w * cross(i.normalWS, i.tangentWS.xyz);
    float3x3 tangentToWorld = float3x3(i.tangentWS.xyz, bitangentWS, i.normalWS);
    return NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tangentToWorld));
}

VRSLMoverVaryings VRSLMoverVertex(VRSLMoverAttributes v)
{
    VRSLMoverVaryings o = (VRSLMoverVaryings)0;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    VRSLMoverPose(v);
    VertexPositionInputs position = GetVertexPositionInputs(v.positionOS.xyz);
    VertexNormalInputs normal = GetVertexNormalInputs(v.normalOS, v.tangentOS);
    o.positionCS = position.positionCS;
    o.positionWS = position.positionWS;
    o.normalWS = normal.normalWS;
    o.tangentWS = float4(normal.tangentWS, v.tangentOS.w * GetOddNegativeScale());
    o.uv = TRANSFORM_TEX(v.uv, _MainTex);
    o.uv1 = v.uv1;
    o.color = v.color;

    #if defined(VRSL_MOVER_FORWARD)
        o.fogFactorAndVertexLight = half4(ComputeFogFactor(position.positionCS.z),
                                          VertexLighting(position.positionWS, normal.normalWS));
        #if defined(VRSL_DMX)
            uint dmx = getDMXChannel();
            o.intensityStrobe = float2(GetDMXIntensity(dmx, 1.0), GetStrobeOutput(dmx));
            o.rgbColor = GetDMXColor(dmx);
        #endif
    #endif
    return o;
}

#if defined(VRSL_MOVER_SHADOW)
float3 _LightDirection;
float3 _LightPosition;

VRSLMoverVaryings VRSLMoverShadowVertex(VRSLMoverAttributes v)
{
    VRSLMoverVaryings o = VRSLMoverVertex(v);
    #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
        float3 lightDirectionWS = normalize(_LightPosition - o.positionWS);
    #else
        float3 lightDirectionWS = _LightDirection;
    #endif
    o.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(o.positionWS, o.normalWS, lightDirectionWS)));
    return o;
}

half4 VRSLMoverShadowFragment(VRSLMoverVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    if (VRSLMoverBodyDiscards(i))
        discard;
    return 0;
}
#endif

half VRSLMoverDepthOnlyFragment(VRSLMoverVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    if (VRSLMoverBodyDiscards(i))
        discard;
    return i.positionCS.z;
}

half4 VRSLMoverDepthNormalsFragment(VRSLMoverVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    if (VRSLMoverBodyDiscards(i))
        discard;
    float3 normalWS = VRSLMoverBodyNormal(i);

    // World space, as URP's own DepthNormals passes write it; octahedral when the renderer
    // asks for that.
    #if defined(_GBUFFER_NORMALS_OCT)
        float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
        return half4(PackFloat2To888(saturate(octNormalWS * 0.5 + 0.5)), 0.0);
    #else
        return half4(normalWS, 0.0);
    #endif
}

#if defined(VRSL_MOVER_FORWARD)
// The lens: UV (0, 0), or the whole body with Pure Emissive on.
bool VRSLMoverIsLens(VRSLMoverVaryings i)
{
    return (i.uv.x == 0.0 && i.uv.y == 0.0) || _PureEmissiveToggle == 1;
}

half3 VRSLMoverLensEmission(VRSLMoverVaryings i)
{
    #if defined(VRSL_DMX)
        half4 emission = getEmissionColor() * (isStrobe() == 1 ? i.intensityStrobe.y : 1.0);
        if (isDMX() == 1)
            emission *= i.rgbColor;
        emission *= _FixtureMaxIntensity * 1500;
        emission = clamp(emission, 0, _LensMaxBrightness * 100);
        if (isDMX() == 1)
        {
            half limit = 0.025;
            if (all(i.rgbColor >= half4(limit, limit, limit, 1)) || i.intensityStrobe.x >= limit)
                emission = lerp(emission, emission * _FixutreIntensityMultiplier, PositivePow(i.intensityStrobe.x, 1.9));
            else
                emission = half4(0, 0, 0, 1);
        }
        emission = lerp(half4(0, 0, 0, emission.w), emission, getGlobalIntensity());
        emission = lerp(half4(0, 0, 0, emission.w), emission, getFinalIntensity());
        return emission.rgb * _UniversalIntensity;
    #else
        half intensities = getGlobalIntensity() * getFinalIntensity() * _UniversalIntensity;
        half4 emission = getEmissionColor() * _FixtureMaxIntensity * 1500;
        emission = clamp(emission, 0, _LensMaxBrightness * 100 * intensities);
        emission = lerp(half4(0, 0, 0, emission.w), emission, GetAudioReactAmplitude());
        emission = lerp(half4(0, 0, 0, emission.w), emission, intensities);
        #if defined(WASH)
            emission = i.uv1.y > 0.0 ? saturate(emission) - 0.25 : emission;
        #endif
        half average = (emission.r + emission.g + emission.b) / 3;
        return lerp(emission.rgb, average.xxx, _Saturation);
    #endif
}

half4 VRSLMoverForwardFragment(VRSLMoverVaryings i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    if (VRSLMoverBodyDiscards(i))
        discard;

    bool lens = VRSLMoverIsLens(i);
    half3 emission = lens
        ? VRSLMoverLensEmission(i)
        : SAMPLE_TEXTURE2D(_DecorativeEmissiveMap, sampler_DecorativeEmissiveMap, i.uv).rgb * _DecorativeEmissiveMapStrength;
    #if defined(VRSL_AUDIOLINK)
        // An AudioLink lens shows its emission alone.
        if (lens)
            return half4(MixFog(emission, i.fogFactorAndVertexLight.x), 1.0);
    #endif

    half4 metallicGloss = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, i.uv);
    SurfaceData surface = (SurfaceData)0;
    surface.albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb * _Color.rgb;
    surface.alpha = 1.0;
    surface.metallic = metallicGloss.r * _Metallic;
    surface.smoothness = metallicGloss.a * _Glossiness;
    surface.occlusion = LerpWhiteTo(SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, i.uv).g, _OcclusionStrength);
    surface.emission = emission;

    InputData input = (InputData)0;
    input.positionWS = i.positionWS;
    input.normalWS = VRSLMoverBodyNormal(i);
    input.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
    input.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
    input.fogCoord = i.fogFactorAndVertexLight.x;
    input.vertexLighting = i.fogFactorAndVertexLight.yzw;
    input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
    input.shadowMask = unity_ProbesOcclusion;
    #if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
        input.bakedGI = SampleProbeVolumePixel(half3(0, 0, 0), GetAbsolutePositionWS(i.positionWS), input.normalWS,
                                               input.viewDirectionWS, i.positionCS.xy);
    #else
        input.bakedGI = SampleSHPixel(half3(0, 0, 0), input.normalWS);
    #endif

    half4 color = UniversalFragmentPBR(input, surface);
    #if defined(_VRSL_LTCGI)
        color.rgb += VRSLLTCGILighting(input, surface, i.uv1);
    #endif
    return half4(MixFog(color.rgb, input.fogCoord), 1.0);
}
#endif

#endif
