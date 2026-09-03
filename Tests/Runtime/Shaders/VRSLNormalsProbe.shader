// Test-only. Copies a prepass texture out of the frame so a row can read it
// back. Pass 0 reads VRSL's own normals, pass 1 URP's; each texel carries the
// normal remapped to 0..1 and, in alpha, whether anything was written there.
// Passes 1 to 4 copy the albedo, the material, the camera depth and the surface
// prepass depth (the two depths as linear 0..1), for whoever has to say which
// of the prepass's inputs went wrong rather than only that its output did; S14
// is the row that will want them.
Shader "Hidden/VRSL-URP/Tests/NormalsProbe"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
        };

        Varyings Vert(uint vertexID : SV_VertexID)
        {
            Varyings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
            o.uv         = GetFullScreenTriangleTexCoord(vertexID);
            return o;
        }

        float4 Encode(float3 n)
        {
            float written = dot(n, n) > 0.01 ? 1.0 : 0.0;
            return float4(n * 0.5 + 0.5, written);
        }
        ENDHLSL

        Pass
        {
            Name "VRSL Normals"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            TEXTURE2D(_VRSLNormalsTexture);
            SAMPLER(sampler_VRSLNormalsTexture);
            float4 Frag(Varyings i) : SV_Target
            {
                return Encode(SAMPLE_TEXTURE2D(_VRSLNormalsTexture, sampler_VRSLNormalsTexture, i.uv).xyz);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Albedo"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            TEXTURE2D(_VRSLAlbedoTexture);
            SAMPLER(sampler_VRSLAlbedoTexture);
            float4 Frag(Varyings i) : SV_Target
            {
                return SAMPLE_TEXTURE2D(_VRSLAlbedoTexture, sampler_VRSLAlbedoTexture, i.uv);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Material"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            TEXTURE2D(_VRSLMaterialTexture);
            SAMPLER(sampler_VRSLMaterialTexture);
            float4 Frag(Varyings i) : SV_Target
            {
                float m = SAMPLE_TEXTURE2D(_VRSLMaterialTexture, sampler_VRSLMaterialTexture, i.uv).r;
                return float4(m, m, m, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Camera Depth"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            float4 Frag(Varyings i) : SV_Target
            {
                float d = Linear01Depth(SampleSceneDepth(i.uv), _ZBufferParams);
                return float4(d, d, d, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Surface Depth"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            TEXTURE2D(_VRSLSurfaceDepthTexture);
            SAMPLER(sampler_VRSLSurfaceDepthTexture);
            float4 Frag(Varyings i) : SV_Target
            {
                float d = Linear01Depth(SAMPLE_TEXTURE2D(_VRSLSurfaceDepthTexture, sampler_VRSLSurfaceDepthTexture, i.uv).r, _ZBufferParams);
                return float4(d, d, d, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "URP Normals"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            TEXTURE2D(_CameraNormalsTexture);
            SAMPLER(sampler_CameraNormalsTexture);
            float4 Frag(Varyings i) : SV_Target
            {
                return Encode(SAMPLE_TEXTURE2D(_CameraNormalsTexture, sampler_CameraNormalsTexture, i.uv).xyz);
            }
            ENDHLSL
        }
    }
}
