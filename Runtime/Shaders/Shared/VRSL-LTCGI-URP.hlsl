#ifndef VRSL_LTCGI_URP
#define VRSL_LTCGI_URP

// LTCGI's area lights on a fixture body, for forward passes built with _VRSL_LTCGI. LTCGI
// (at.pimaker.ltcgi) is optional: include this file only inside that keyword's #if, so a
// project without the package never opens LTCGI's files.
//
// The material's "LTCGI" subshader tag names the _VRSL_LTCGI property, which is how LTCGI's
// controller finds the renderers to light. lightmapUV is the mesh's raw UV1, which LTCGI
// reads only for a renderer its shadowmap bake covered.

#include "Packages/at.pimaker.ltcgi/Shaders/LTCGI_URP_Compat.hlsl"
#include "Packages/at.pimaker.ltcgi/Shaders/LTCGI.cginc"

// What the screens add, shaded with the BRDF terms URP uses for its own lights. LTCGI's
// magnitude table carries no Fresnel, so specular takes URP's environment Fresnel.
half3 VRSLLTCGILighting(InputData input, SurfaceData surface, float2 lightmapUV)
{
    half alpha = surface.alpha;
    BRDFData brdf;
    InitializeBRDFData(surface.albedo, surface.metallic, surface.specular, surface.smoothness, alpha, brdf);

    half3 diffuse = 0;
    half3 specular = 0;
    LTCGI_Contribution(input.positionWS, input.normalWS, input.viewDirectionWS, brdf.perceptualRoughness,
                       lightmapUV, diffuse, specular);

    half fresnel = Pow4(1.0 - saturate(dot(input.normalWS, input.viewDirectionWS)));
    half3 specularColor = lerp(brdf.specular, brdf.grazingTerm, fresnel);
    AmbientOcclusionFactor ao = CreateAmbientOcclusionFactor(input, surface);
    return (diffuse * brdf.diffuse + specular * specularColor) * ao.directAmbientOcclusion;
}

#endif
