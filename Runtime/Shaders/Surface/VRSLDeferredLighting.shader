// Fullscreen additive lighting pass for VRSL URP realtime lights.
// Runs after URP opaque rendering. Reconstructs world-space position from
// _CameraDepthTexture and reads the surface normal from _VRSLNormalsTexture
// (written by VRSLNormalsPrepass via the standard URP DepthNormals shader
// tags); on pixels where the prepass wrote no normal — surfaces drawn by
// shaders without a URP DepthNormals pass — falls back to a normal derived
// from screen-space derivatives of the depth-reconstructed position.
Shader "Hidden/VRSL-URP/DeferredLighting"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "VRSL_Lighting"

            // Additive — output is added on top of the existing frame color
            Blend One One
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
            #include "Packages/net.towneh.vrsl-urp/Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl"

            // Set by the manager's LightingPass via SetGlobal* before DrawMesh
            StructuredBuffer<VRSLLightData> _VRSLLights;
            uint _VRSLLightCount;

            // VRSL-owned normals RT, written by VRSLNormalsPrepass and bound
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

                // Read the authored surface normal from VRSL's normals prepass.
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
                    normalWS = normalize(cross(ddy(posWS), ddx(posWS)));
                else
                    normalWS = normalize(normalWS);

                // Accumulate contributions from all VRSL lights
                float3 lighting = 0;
                for (uint li = 0; li < _VRSLLightCount; li++)
                {
                    VRSLLightData light = _VRSLLights[li];
                    float3 contrib = VRSL_EvaluateLight(light, posWS, normalWS);

                    // Apply gobo projection (goboAndSpin.x = slice index, .y = spin speed)
                    contrib *= SampleGobo(light.goboAndSpin.x, light.goboAndSpin.y,
                                          posWS,
                                          light.positionAndRange.xyz,
                                          light.directionAndType.xyz,
                                          light.spotCosines.y,
                                          light.spotCosines.w);

                    lighting += contrib;
                }

                return float4(lighting, 0.0);
            }
            ENDHLSL
        }
    }
}
