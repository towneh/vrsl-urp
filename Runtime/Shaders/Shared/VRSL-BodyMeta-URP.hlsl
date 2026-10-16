#ifndef VRSL_BODY_META_URP
#define VRSL_BODY_META_URP

// The Meta pass of a static fixture body: what the lightmapper reads when the body is marked
// to contribute to baked lighting. Without it the body bakes as a white surface. The housing
// is baked the way URP Lit bakes a surface with the same colour, metallic and smoothness. Its
// glow is left out, since DMX or AudioLink drives it at runtime and a bake would keep whatever
// was playing then. A pass includes Core.hlsl, Lighting.hlsl and VRSL-Defines-URP.hlsl first,
// defining VRSL_SURFACE for the bodies that read a metallic-smoothness map.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/MetaPass.hlsl"

struct VRSLMetaAttributes
{
    float4 positionOS : POSITION;
    float2 uv0        : TEXCOORD0;
    float2 uv1        : TEXCOORD1;
    float2 uv2        : TEXCOORD2;
};

struct VRSLMetaVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
#ifdef EDITOR_VISUALIZATION
    float2 VizUV      : TEXCOORD1;
    float4 LightCoord : TEXCOORD2;
#endif
};

VRSLMetaVaryings VRSLBodyMetaVertex(VRSLMetaAttributes input)
{
    VRSLMetaVaryings output = (VRSLMetaVaryings)0;
    output.positionCS = UnityMetaVertexPosition(input.positionOS.xyz, input.uv1, input.uv2);
    output.uv = TRANSFORM_TEX(input.uv0, _MainTex);
#ifdef EDITOR_VISUALIZATION
    UnityEditorVizData(input.positionOS.xyz, input.uv0, input.uv1, input.uv2, output.VizUV, output.LightCoord);
#endif
    return output;
}

half4 VRSLBodyMetaFragment(VRSLMetaVaryings input) : SV_Target
{
    half3 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb * _Color.rgb;
    half metallic = _Metallic;
    half smoothness = _Glossiness;
#if defined(VRSL_SURFACE)
    half4 metallicSmoothness = SAMPLE_TEXTURE2D(_MetallicSmoothness, sampler_MetallicSmoothness, input.uv);
    metallic *= metallicSmoothness.r;
    smoothness *= metallicSmoothness.a;
#endif

    // URP Lit's metallic BRDF and Meta albedo, written out because URP's BRDF initialisers
    // differ between the versions this package supports.
    half3 diffuse = albedo * OneMinusReflectivityMetallic(metallic);
    half3 specular = lerp(kDielectricSpec.rgb, albedo, metallic);
    half roughness = max(PerceptualRoughnessToRoughness(PerceptualSmoothnessToPerceptualRoughness(smoothness)), HALF_MIN_SQRT);

    UnityMetaInput metaInput = (UnityMetaInput)0;
    metaInput.Albedo = diffuse + specular * roughness * 0.5;
#ifdef EDITOR_VISUALIZATION
    metaInput.VizUV = input.VizUV;
    metaInput.LightCoord = input.LightCoord;
#endif
    return UnityMetaFragment(metaInput);
}

#endif
