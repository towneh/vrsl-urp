// Fullscreen deferred lighting for VRSL URP realtime lights. Runs after URP
// opaque rendering, reading the surface data VRSLSurfacePrepass captured:
//
//   _CameraDepthTexture    world position, reconstructed
//   _VRSLNormalsTexture    authored world normal (falls back to a depth
//                          derivative where no prepass normal was written)
//   _VRSLAlbedoTexture     rgb = base colour, a = smoothness
//   _VRSLMaterialTexture   r   = metallic
//
// Each light is evaluated through URP's own BRDF, so a VRSL fixture shades a Lit
// material the way a URP spot light would: the surface keeps its texture colour,
// metals stay metallic, and glossy surfaces get a specular highlight.
//
// Lights come from the per-tile list VRSLLightCull.compute builds, so per-pixel
// cost tracks the fixtures whose range actually reaches the tile rather than the
// fixture count of the whole scene. When the cull pass hasn't run the shader
// falls back to iterating every light.
Shader "Hidden/VRSL-URP/DeferredLighting"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "VRSL_Lighting"

            // Additive — output is added on top of the existing frame color.
            // ColorMask RGB: never write alpha. The scene alpha is meaningful in some
            // hosts (Basis mirrors / compositing), and an additive pass that disturbs it
            // (even via MSAA resolve / HDR-format quirks) makes lit opaque surfaces read
            // as see-through. We only ever contribute RGB light, so mask alpha out.
            Blend One One
            ColorMask RGB
            ZWrite Off
            ZTest Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5
            // Single-pass-instanced (VR) variant. URP's XR system applies
            // SetInstanceMultiplier(viewCount) before our DrawMesh hits the
            // command buffer; the macros below route per-instance writes to
            // the correct eye slice of the camera color target.
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSLSurfaceBRDF.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSLTileCulling.hlsl"

            // Set by the manager's LightingPass via SetGlobal* before DrawMesh
            StructuredBuffer<VRSLLightData> _VRSLLights;
            uint  _VRSLLightCount;

            // VRSL-owned normals RT, written by VRSLSurfacePrepass and bound
            // globally via SetGlobalTextureAfterPass. Sampled with the SPI VR
            // texture macros so the per-eye Tex2DArray slice is selected
            // automatically under stereo rendering.
            TEXTURE2D_X(_VRSLNormalsTexture);
            SAMPLER(sampler_VRSLNormalsTexture);

            // Mesh-driven attributes — manager renders RenderingUtils.fullscreenMesh,
            // whose vertices are already in clip space (-1..1) so the vertex shader
            // is a near pass-through.
            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionCS = float4(input.positionOS, 1.0);
                o.uv         = input.uv;
#if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1.0 - o.uv.y;
#endif
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 uv = i.uv;

                // Skip skybox / far-plane pixels
                float rawDepth = SampleSceneDepth(uv);
#if UNITY_REVERSED_Z
                if (rawDepth < 0.0001) return 0;
#else
                if (rawDepth > 0.9999) return 0;
#endif

                // Reconstruct world position from depth.
                float3 posWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

                // Read the authored surface normal from VRSL's surface prepass.
                // Any opaque shader with a DepthNormals/DepthNormalsOnly pass
                // contributes — URP Lit, Poiyomi URP, lilToon URP, Mochie URP.
                // Pixels with no prepass-written normal fall through to a
                // depth-derivative reconstruction so the surface still picks
                // up VRSL light, just faceted to the underlying tessellation.
                // Unity unifies the screen-Y convention for ddx/ddy across
                // graphics APIs, so the cross-product order is the same on
                // D3D, Vulkan, Metal, and OpenGL.
                float3 normalWS = SAMPLE_TEXTURE2D_X(_VRSLNormalsTexture,
                    sampler_VRSLNormalsTexture, uv).xyz;
                if (dot(normalWS, normalWS) < 0.01)
                {
                    // Depth-derivative normal for surfaces that don't write the VRSL
                    // normals prepass (shaders with no DepthNormals pass, e.g. LTCGI
                    // Simple URP). Guard the normalize: on large flat surfaces at grazing
                    // angles / depth discontinuities the cross product collapses to ~0,
                    // and normalize(0) is NaN — which the additive blend then smears across
                    // the frame as garbage ("see-through") pixels. Fall back to world-up.
                    float3 dn  = cross(ddy(posWS), ddx(posWS));
                    float  dl2 = dot(dn, dn);
                    normalWS = dl2 > 1e-12 ? dn * rsqrt(dl2) : float3(0.0, 1.0, 0.0);
                }
                else
                    normalWS = normalize(normalWS);

                float3 viewDirWS = GetWorldSpaceNormalizeViewDir(posWS);

                // Material inputs are constant across the light loop, so the
                // BRDF setup is hoisted out of it.
                BRDFData brdfData = VRSL_GetSurfaceBRDF(uv);

                // One dither value per pixel, reused by every light's trace.
                float shadowDither = InterleavedGradientNoise(i.positionCS.xy,
                                                              (int)(_Time.y * 60.0));

                uint tileIndex = VRSL_TileIndex(uv, VRSL_EyeIndex());
                uint lightCount = VRSL_LightListCount(tileIndex, _VRSLLightCount);

                float3 lighting = 0;
                // [loop] rather than letting the compiler choose: the body now
                // carries a gobo fetch and a contact-shadow trace, and d3d11
                // tries to unroll against lightCount, overruns its budget and
                // fails the whole shader with "unable to unroll loop". The
                // count is dynamic per tile anyway, so unrolling was never
                // going to help.
                [loop]
                for (uint slot = 0; slot < lightCount; slot++)
                {
                    VRSLLightData light = _VRSLLights[VRSL_LightListIndex(tileIndex, slot)];

                    float3 contrib = VRSL_EvaluateLightPBR(
                        light, posWS, normalWS, viewDirWS, brdfData);

                    // A gobo can only ever reduce the contribution, so the
                    // texture-array fetch — the most expensive part of the loop —
                    // is skipped wherever the light already reaches nothing.
                    if (any(contrib > 0.0))
                        contrib *= SampleGobo(VRSL_GoboIndex(light), light.spotParams.w,
                                              posWS,
                                              light.positionAndRange.xyz,
                                              light.directionAndType.xyz,
                                              light.spotParams.y,
                                              light.spotParams.z);

                    // Costliest term in the loop — a depth-buffer march per
                    // light — so it runs last, only for lights still reaching
                    // this pixel, and compiles out entirely at strength 0.
                    if (any(contrib > 0.0))
                    {
                        float3 toLight = light.positionAndRange.xyz - posWS;
                        float  distToLight = length(toLight);
                        contrib *= VRSL_ContactShadow(posWS,
                                                      toLight / max(distToLight, 1e-4),
                                                      distToLight, shadowDither);
                    }

                    lighting += contrib;
                }

                return float4(lighting, 0.0);
            }
            ENDHLSL
        }
    }
}
