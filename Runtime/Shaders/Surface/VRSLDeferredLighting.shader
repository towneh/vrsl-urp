// Fullscreen lighting shader for VRSL URP realtime lights. Runs after URP
// opaque rendering and contains two passes:
// (rev: NaN-safe light direction + degenerate-normal guard)
//
//   Pass 0 (VRSL_Lighting) — additive surface-lighting pass. Reconstructs
//   world-space position from _CameraDepthTexture and reads the surface
//   normal from _VRSLNormalsTexture (written by VRSLNormalsPrepass via the
//   standard URP DepthNormals shader tags); on pixels where the prepass
//   wrote no normal — surfaces drawn by shaders without a URP DepthNormals
//   pass — falls back to a normal derived from screen-space derivatives of
//   the depth-reconstructed position. Optionally modulates each light's
//   contribution by a pre-light scene-colour snapshot bound at
//   _VRSLOpaqueTexture, as a coarse albedo proxy when _VRSLAlbedoTint > 0.
//
//   Pass 1 (VRSL_OpaqueCapture) — fullscreen blit. Samples the source
//   texture bound at _VRSLBlitSource (the active camera colour target) and
//   writes it to the bound render attachment (a VRSL-owned RT used as the
//   albedo-tint snapshot). Skipped at the C# level when the tint strength
//   is zero, so no cost when the feature is off. URP's own _CameraOpaqueTexture
//   isn't reliable here — under URP 17 render graph mode CopyColor doesn't
//   always run for our pass, so the package captures its own snapshot.
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
            #include "Packages/net.towneh.vrsl-urp/Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl"

            // Set by the manager's LightingPass via SetGlobal* before DrawMesh
            StructuredBuffer<VRSLLightData> _VRSLLights;
            uint  _VRSLLightCount;
            // Albedo-tint strength. 0 = pure additive (light added on top of the
            // surface unmodulated, can read as washed-out under bright spots).
            // 1 = pure multiplicative against the pre-light scene colour
            // (physically closer to surface reflectance — light picks up the
            // surface's hue and dark surfaces stay dark). When > 0 the lighting
            // pass also runs a capture sub-pass that populates _VRSLOpaqueTexture
            // with a pre-VRSL colour snapshot.
            float _VRSLAlbedoTint;

            // VRSL-owned normals RT, written by VRSLNormalsPrepass and bound
            // globally via SetGlobalTextureAfterPass. Sampled with the SPI VR
            // texture macros so the per-eye Tex2DArray slice is selected
            // automatically under stereo rendering.
            TEXTURE2D_X(_VRSLNormalsTexture);
            SAMPLER(sampler_VRSLNormalsTexture);

            // VRSL-owned albedo-proxy RT. The opaque-capture sub-pass blits the
            // active camera colour into this snapshot before lighting accumulates;
            // sampled here when _VRSLAlbedoTint > 0 to modulate each light's
            // surface contribution by the pre-light scene colour.
            TEXTURE2D_X(_VRSLOpaqueTexture);
            SAMPLER(sampler_VRSLOpaqueTexture);

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

                // Modulate the accumulated additive contribution by the pre-light
                // scene colour (sampled from VRSL's own opaque snapshot, captured
                // by the preceding VRSL_OpaqueCapture sub-pass). Acts as an albedo
                // proxy — true albedo isn't available in this lightweight prepass,
                // but the opaque copy works as a stand-in in the ambient-dominated
                // stage scenes the package targets.
                if (_VRSLAlbedoTint > 0.0)
                {
                    float3 sceneColor = SAMPLE_TEXTURE2D_X(_VRSLOpaqueTexture,
                        sampler_VRSLOpaqueTexture, uv).rgb;
                    lighting *= lerp(float3(1.0, 1.0, 1.0), sceneColor, _VRSLAlbedoTint);
                }

                return float4(lighting, 0.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "VRSL_OpaqueCapture"

            // Plain copy — destination is the VRSL-owned snapshot RT, source is
            // the active camera colour. No blending, no depth interaction.
            Blend Off
            ZWrite Off
            ZTest Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_VRSLBlitSource);
            SAMPLER(sampler_VRSLBlitSource);

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
                return SAMPLE_TEXTURE2D_X(_VRSLBlitSource, sampler_VRSLBlitSource, i.uv);
            }
            ENDHLSL
        }
    }
}
