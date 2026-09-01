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
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl"

TEXTURE2D_X(_VRSLAlbedoTexture);
SAMPLER(sampler_VRSLAlbedoTexture);
TEXTURE2D_X(_VRSLMaterialTexture);
SAMPLER(sampler_VRSLMaterialTexture);

// Depth the surface prepass drew at, used to reject albedo the camera didn't
// actually keep. See VRSL_SurfaceDataCovers below.
TEXTURE2D_X(_VRSLSurfaceDepthTexture);
SAMPLER(sampler_VRSLSurfaceDepthTexture);

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

// Does the prepass's surface at this pixel belong to the surface the camera
// actually shows?
//
// The albedo prepass draws through DrawingSettings.overrideShader, which
// replaces the material's own shader outright. Any visibility logic living in
// that shader therefore never runs: Poiyomi's UDIM discard (which returns NaN
// from its vertex program, collapsing the triangle in the real passes), custom
// alpha clips, vertex displacement. The prepass happily draws geometry the
// camera dropped, and since the lighting pass takes position from the camera's
// depth, the result is the surface *behind* being lit through the albedo of the
// thing that was supposed to be invisible — the discarded shape reappears as
// lit colour.
//
// This is a backstop. The prepass already clips against the camera's depth as it
// rasterises (see VRSLSurfaceProperties), which is what actually fixes the case
// above — rejecting here alone cannot, because by this point the hidden surface
// has won the prepass depth test and the albedo behind it is gone, leaving only
// the neutral fallback to substitute. What this catches is geometry the clip
// can't, such as a shader whose real vertices are displaced somewhere else.
//
// Compared in linear eye space on the shared tolerance, so the gate and the
// backstop can't disagree about what counts as the same surface.
bool VRSL_SurfaceDataCovers(float2 uv, float cameraRawDepth)
{
    float prepassRaw = SAMPLE_TEXTURE2D_X(
        _VRSLSurfaceDepthTexture, sampler_VRSLSurfaceDepthTexture, uv).r;

    // Nothing drawn here by the prepass: cleared depth is 0 in reversed-Z and 1
    // otherwise. The albedo read handles this case on its own, so defer to it.
#if UNITY_REVERSED_Z
    if (prepassRaw <= 0.0) return true;
#else
    if (prepassRaw >= 1.0) return true;
#endif

    float prepassEye = LinearEyeDepth(prepassRaw,      _ZBufferParams);
    float cameraEye  = LinearEyeDepth(cameraRawDepth,  _ZBufferParams);

    return abs(prepassEye - cameraEye) <= VRSL_SURFACE_DEPTH_TOLERANCE(cameraEye);
}

// Build the per-pixel BRDF inputs once, outside the light loop.
BRDFData VRSL_GetSurfaceBRDF(float2 uv, float cameraRawDepth)
{
    half3 albedo     = VRSL_FALLBACK_ALBEDO;
    half  smoothness = VRSL_FALLBACK_SMOOTHNESS;
    half  metallic   = 0;

    if (_VRSLSurfaceDataValid > 0.5 && VRSL_SurfaceDataCovers(uv, cameraRawDepth))
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

// ──────────────────────────────────────────────────────────────────────────────
// Screen-space contact shadows
// ──────────────────────────────────────────────────────────────────────────────

// x = strength (0 disables), y = max trace distance in metres, z = step count,
// w = depth thickness in metres.
float4 _VRSLContactShadowParams;

// March the depth buffer from the shaded point towards the light and report how
// much of the path was blocked. 1 = fully lit, 0 = fully occluded.
//
// This is contact shadowing, not shadow mapping: it only knows about geometry
// the camera can see, and only within the trace distance. An avatar standing in
// a beam shadows the floor at its feet; a wall between a fixture and a surface
// across the room does not, and neither does an occluder off the side of the
// screen. Those need a light-perspective shadow map, which this pipeline
// deliberately doesn't have.
//
// Positions are projected with ComputeNormalizedDeviceCoordinatesWithZ, the
// exact inverse of the ComputeWorldSpacePosition call the caller reconstructed
// posWS with, so the trace can't drift against the depth buffer it samples.
float VRSL_ContactShadow(float3 posWS, float3 lightDirWS, float distanceToLight, float dither)
{
    float strength = _VRSLContactShadowParams.x;
    if (strength <= 0.0) return 1.0;

    // A zero step count means the trace should not run, independently of strength.
    // Both are zeroed together today; read separately so a caller that zeroes one
    // does not silently get a one-step trace out of the clamp below.
    int stepCount = (int)_VRSLContactShadowParams.z;
    if (stepCount <= 0) return 1.0;
    float traceDist = min(_VRSLContactShadowParams.y, distanceToLight);
    if (traceDist <= 1e-4) return 1.0;

    float  thickness = _VRSLContactShadowParams.w;
    float  stepSize  = traceDist / stepCount;
    float3 rayStep   = lightDirWS * stepSize;

    // Start a dithered fraction of a step along, so the banding the fixed step
    // size would otherwise produce breaks up into noise the eye reads as grain.
    float3 p = posWS + rayStep * (0.5 + dither);

    [loop]
    for (int s = 0; s < stepCount; s++, p += rayStep)
    {
        float3 ndc = ComputeNormalizedDeviceCoordinatesWithZ(p, GetWorldToHClipMatrix());
        if (any(ndc.xy < 0.0) || any(ndc.xy > 1.0)) break;   // walked off screen

        float sceneEye  = LinearEyeDepth(SampleSceneDepth(ndc.xy), _ZBufferParams);
        float sampleEye = LinearEyeDepth(ndc.z, _ZBufferParams);

        // Positive means the ray is behind whatever the camera sees here. The
        // thickness window stops distant background from acting as an occluder,
        // since a depth buffer records surfaces rather than solids.
        float delta = sampleEye - sceneEye;
        if (delta > stepSize && delta < thickness)
            return 1.0 - strength;
    }

    return 1.0;
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
    if (!VRSL_IsActive(light)) return 0;

    float3 toLight = light.positionAndRange.xyz - posWS;
    float  distSq  = dot(toLight, toLight);
    float  range   = light.positionAndRange.w;
    if (distSq > range * range) return 0;

    float distAtten = VRSL_DistanceAttenuation(distSq, range);

    float spotAtten = 1.0;
    if (VRSL_LightType(light) < 0.5)
        spotAtten = VRSL_SpotAttenuation(
            light.directionAndType.xyz, toLight,
            light.spotParams.x, light.spotParams.y,
            light.spotParams.z);

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
