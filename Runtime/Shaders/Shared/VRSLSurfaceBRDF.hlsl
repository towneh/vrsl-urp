#ifndef VRSL_SURFACE_BRDF_INCLUDED
#define VRSL_SURFACE_BRDF_INCLUDED

// Surface response for VRSL lights, evaluated through URP's own BRDF so a VRSL
// fixture shades a Lit material the way a URP spot light would. Requires URP's
// Core.hlsl to have been included first, which is why this lives apart from
// VRSLLightingLibrary.hlsl — the compute kernels include that header for the
// struct layouts and must not drag the BRDF library in with it.
//
// Material inputs come from VRSLSurfacePrepass:
//   _VRSLAlbedoTexture    rgb = base colour, a = smoothness
//   _VRSLMaterialTexture  r   = metallic

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/BRDF.hlsl"
#include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl"

TEXTURE2D_X(_VRSLAlbedoTexture);
SAMPLER(sampler_VRSLAlbedoTexture);
TEXTURE2D_X(_VRSLMaterialTexture);
SAMPLER(sampler_VRSLMaterialTexture);

// 1 when the surface prepass ran and its targets are bound, 0 otherwise. Set by
// the lighting pass. Without it the shader would be reading whatever an unbound
// texture slot resolves to, which differs per graphics API — a white default
// would read as a mirror-smooth metal rather than as missing data.
float _VRSLSurfaceDataValid;

// Fallback surface for pixels with no prepass data — geometry drawn by shaders
// with no forward LightMode tag, or a scene running without the prepass shader
// assigned. A mid-grey dielectric keeps those surfaces responding to light
// instead of dropping to black.
#define VRSL_FALLBACK_ALBEDO     half3(0.5, 0.5, 0.5)
#define VRSL_FALLBACK_SMOOTHNESS half(0.0)

// Build the per-pixel BRDF inputs once, outside the light loop.
BRDFData VRSL_GetSurfaceBRDF(float2 uv)
{
    half3 albedo     = VRSL_FALLBACK_ALBEDO;
    half  smoothness = VRSL_FALLBACK_SMOOTHNESS;
    half  metallic   = 0;

    if (_VRSLSurfaceDataValid > 0.5)
    {
        half4 albedoSmoothness = SAMPLE_TEXTURE2D_X(
            _VRSLAlbedoTexture, sampler_VRSLAlbedoTexture, uv);

        // The prepass clears to transparent black, so a pixel it never drew
        // reads as zero across the board. Treat that as "no data" rather than
        // as a perfectly black surface, which would swallow the light entirely.
        if (dot(albedoSmoothness.rgb, half3(1, 1, 1)) > 0)
        {
            albedo     = albedoSmoothness.rgb;
            smoothness = albedoSmoothness.a;
            metallic   = SAMPLE_TEXTURE2D_X(
                _VRSLMaterialTexture, sampler_VRSLMaterialTexture, uv).r;
        }
    }

    half alpha = 1;
    BRDFData brdfData;
    InitializeBRDFData(albedo, metallic, half3(0, 0, 0), smoothness, alpha, brdfData);
    return brdfData;
}

// Evaluate one VRSL light against the surface. Mirrors URP's
// LightingPhysicallyBased: radiance = lightColor * attenuation * NdotL, shaped
// by the material's diffuse and specular lobes.
//
// Returns exactly zero whenever the light cannot reach the pixel, so the caller
// can skip the gobo texture fetch — the fetch is otherwise the dominant cost of
// the loop, and a gobo can only ever reduce the result.
float3 VRSL_EvaluateLightPBR(VRSLLightData light, float3 posWS, float3 normalWS,
                             float3 viewDirWS, BRDFData brdfData)
{
    if (light.spotCosines.z < 0.5) return 0;

    float3 toLight = light.positionAndRange.xyz - posWS;
    float  distSq  = dot(toLight, toLight);
    float  range   = light.positionAndRange.w;
    if (distSq > range * range) return 0;

    float distAtten = VRSL_DistanceAttenuation(distSq, range);

    float spotAtten = 1.0;
    if (light.directionAndType.w < 0.5)
        spotAtten = VRSL_SpotAttenuation(
            light.directionAndType.xyz, toLight,
            light.spotCosines.x, light.spotCosines.y,
            light.spotCosines.w);

    float atten = distAtten * spotAtten;
    if (atten < 1e-5) return 0;

    // NaN-safe: normalize(toLight) is NaN when a surface sits at the light
    // position, which happens whenever a point-light origin lands on the bar
    // mesh next to a floor or ceiling.
    float3 lightDirWS = toLight * rsqrt(max(distSq, 0.0001));

    float NdotL = saturate(dot(normalWS, lightDirWS));
    if (NdotL <= 0) return 0;

    float3 radiance = light.colorAndIntensity.xyz * light.colorAndIntensity.w
                      * (atten * NdotL);

    return DirectBRDF(brdfData, normalWS, lightDirWS, viewDirWS) * radiance;
}

#endif // VRSL_SURFACE_BRDF_INCLUDED
