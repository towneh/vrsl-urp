Shader "VRSL-URP/Standard Static/Surface Shaders/Opaque"
{
    Properties
    {
         [Toggle] _EnableDMX ("Enable Stream DMX/DMX Control", Int) = 0
        _DMXChannel ("Starting DMX Channel", Int) = 0
        [HideInInspector][Toggle] _NineUniverseMode ("Extended Universe Mode", Int) = 0
		//[NoScaleOffset] _VRSLU_DMXGridRenderTexture("DMX Grid Render Texture (RAW Unsmoothed)", 2D) = "white" {}
		//[NoScaleOffset] _VRSLU_DMXGridRenderTextureMovement("DMX Grid Render Texture (To Control Lights)", 2D) = "white" {}
		//[NoScaleOffset] _VRSLU_DMXGridStrobeTimer("DMX Grid Render Texture (For Strobe Timings", 2D) = "white" {}
       
		[Toggle] _EnableStrobe ("Enable Strobe", Int) = 0
		 //[HideInInspector][Toggle] _EnableDMX ("Enable Stream DMX/DMX Control", Int) = 0
		[HideInInspector]_StrobeFreq("Strobe Frequency", Range(0,25)) = 1
		[HideInInspector][Toggle] _EnableSpin("Enable Auto Spinning", Float) = 0  

        [HDR]_Emission("Light Color Tint", Color) = (1,1,1,1)     
        _EmissionMask ("Emission Mask", 2D) = "white" {}   
        _FixtureMaxIntensity ("Maximum Light Intensity",Range (0,55)) = 1
        _Saturation ("Color Saturation", Range (0,1)) = 0.95
        _GlobalIntensity("Global Intensity", Range(0,1)) = 1
        _GlobalIntensityBlend("Global Intensity Blend", Range(0,1)) = 1
        _FinalIntensity("Final Intensity", Range(0,1)) = 1
        _UniversalIntensity ("Universal Intensity", Range (0,1)) = 1
        
        
        _CurveMod ("Light Intensity Multiplier", Range (1,50)) = 5.0
        
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _MetallicSmoothness ("Metallic(R) / Smoothness(A) Map", 2D) = "white" {}
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _Metallic ("Metallic Blend", Range(0,1)) = 0.0
        _Glossiness ("Smoothness Blend", Range(0,1)) = 0.5
        [Enum(1CH,0,4CH,1,5CH,2,13CH,3)] _ChannelMode ("Channel Mode", Int) = 2
        [Toggle(_VRSL_LTCGI)] _VRSL_LTCGI ("Lit by LTCGI Screens (needs the LTCGI package)", Float) = 0

    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "LTCGI" = "_VRSL_LTCGI"
        }
        LOD 100

        // Forward Pass
        Pass
        {
            Name "ForwardLit"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // The DMX channel buffers need shader model 5.
            #pragma target 4.5

            #define VRSL_DMX
            #define VRSL_SURFACE
            #pragma shader_feature_local _ _1CH_MODE _4CH_MODE _5CH_MODE _13CH_MODE
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #pragma shader_feature_local _ _VRSL_LTCGI

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #include "../Shared/VRSL-Defines-URP.hlsl"
            #include "../Shared/VRSL-DMXFunctions-URP.hlsl"
            #include "VRSL-StandardSurface-Functions-URP.hlsl"
            #if defined(_VRSL_LTCGI)
                #include "../Shared/VRSL-LTCGI-URP.hlsl"
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                #if defined(_VRSL_LTCGI)
                    float2 uv1 : TEXCOORD1;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float4 tangentWS : TEXCOORD3;
                half3 vertexLight : TEXCOORD4;
                #if defined(_VRSL_LTCGI)
                    float2 uv1 : TEXCOORD5;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                // Positions
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;

                // Normals and tangents
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.normalWS = normalInputs.normalWS;
                output.vertexLight = VertexLighting(posInputs.positionWS, normalInputs.normalWS);

                // Calculate tangent for normal mapping
                real sign = input.tangentOS.w * GetOddNegativeScale();
                output.tangentWS = float4(normalInputs.tangentWS.xyz, sign);

                // UVs
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                #if defined(_VRSL_LTCGI)
                    output.uv1 = input.uv1;
                #endif
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                // Sample textures
                half4 baseMap = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                half4 metallicSmoothness = SAMPLE_TEXTURE2D(_MetallicSmoothness, sampler_MetallicSmoothness, input.uv);
                half4 color = baseMap * _Color;

                float3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv));

                // Unnormalised basis, as in URP Lit: a mesh without tangents falls back to
                // its vertex normal.
                float3 bitangentWS = input.tangentWS.w * cross(input.normalWS, input.tangentWS.xyz);
                float3x3 tangentToWorld = float3x3(input.tangentWS.xyz, bitangentWS, input.normalWS);
                float3 normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tangentToWorld));

                // Surface data
                InputData lightingInput = (InputData)0;
                lightingInput.normalWS = normalWS;
                lightingInput.positionWS = input.positionWS;
                lightingInput.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionHCS);
                lightingInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                lightingInput.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
                    lightingInput.bakedGI = SampleProbeVolumePixel(half3(0, 0, 0), GetAbsolutePositionWS(input.positionWS),
                                                                   normalWS, lightingInput.viewDirectionWS, input.positionHCS.xy);
                #else
                    lightingInput.bakedGI = SampleSH(normalWS);
                #endif
                lightingInput.vertexLighting = input.vertexLight;

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = color.rgb;
                surfaceData.alpha = color.a;
                surfaceData.occlusion = 1;

                // Metallic in R channel, Smoothness in A channel (standard convention)
                surfaceData.metallic = metallicSmoothness.r * _Metallic;
                surfaceData.smoothness = metallicSmoothness.a * _Glossiness;

                // Emission from DMX, masked by the emission mask
                float2 emUV = TRANSFORM_TEX(input.uv, _EmissionMask);
                #if _1CH_MODE
                    surfaceData.emission = GetDMXEmission1CH(emUV) * _CurveMod;
                #elif _4CH_MODE
                    surfaceData.emission = GetDMXEmission4CH(emUV) * _CurveMod;
                #elif _5CH_MODE
                    surfaceData.emission = GetDMXEmission5CH(emUV) * _CurveMod;
                #elif _13CH_MODE
                    surfaceData.emission = GetDMXEmission13CH(emUV) * _CurveMod;
                #endif

                surfaceData.normalTS = normalTS;

                // Apply lighting
                half4 litColor = UniversalFragmentPBR(lightingInput, surfaceData);
                #if defined(_VRSL_LTCGI)
                    litColor.rgb += VRSLLTCGILighting(lightingInput, surfaceData, input.uv1);
                #endif
                return litColor;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #include "../Shared/VRSL-Defines-URP.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 texcoord : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            float3 _LightDirection;

            float4 GetShadowPositionHClip(Attributes input)
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));

                #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                return positionCS;
            }

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                output.positionCS = GetShadowPositionHClip(input);
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_TARGET
            {
                return 0;
            }
            ENDHLSL
        }

        // DepthOnly Pass, Required for depth priming
        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #include "../Shared/VRSL-Defines-URP.hlsl"

            struct Attributes
            {
                float4 position : POSITION;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.position.xyz);
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return 0;
            }
            ENDHLSL
        }

        // DepthNormals Pass, Required for depth priming
        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormals"
            }

            ZWrite On

            HLSLPROGRAM
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment

            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #include "../Shared/VRSL-Defines-URP.hlsl"

            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 tangentWS : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.texcoord, _MainTex);

                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.normalWS = normalInput.normalWS;

                real sign = input.tangentOS.w * GetOddNegativeScale();
                output.tangentWS = float4(normalInput.tangentWS.xyz, sign);
                return output;
            }

            half4 DepthNormalsFragment(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                // Read normal map
                float3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv));

                // Unnormalised basis, as in URP Lit: a mesh without tangents falls back to
                // its vertex normal.
                float3 bitangentWS = input.tangentWS.w * cross(input.normalWS, input.tangentWS.xyz);
                float3x3 tangentToWorld = float3x3(input.tangentWS.xyz, bitangentWS, input.normalWS);
                float3 normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tangentToWorld));

                // World space, as URP's own DepthNormals passes write it and every reader of the
                // normals texture decodes it; octahedral when the renderer asks for that.
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                    return half4(PackFloat2To888(saturate(octNormalWS * 0.5 + 0.5)), 0.0);
                #else
                    return half4(normalWS, 0.0);
                #endif
            }
            ENDHLSL
        }

        // Meta Pass, for baked lighting
        Pass
        {
            Name "Meta"
            Tags
            {
                "LightMode" = "Meta"
            }

            Cull Off

            HLSLPROGRAM
            #pragma vertex VRSLBodyMetaVertex
            #pragma fragment VRSLBodyMetaFragment
            #pragma shader_feature EDITOR_VISUALIZATION

            #define VRSL_SURFACE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #include "../Shared/VRSL-Defines-URP.hlsl"
            #include "../Shared/VRSL-BodyMeta-URP.hlsl"
            ENDHLSL
        }
    }
}
