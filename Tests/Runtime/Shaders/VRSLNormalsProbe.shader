// Test-only. Copies a normals texture out of the frame so a row can read it
// back: pass 0 reads VRSL's own prepass output, pass 1 reads URP's. Each texel
// carries the normal remapped to 0..1 and, in alpha, whether anything was
// written there at all.
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
