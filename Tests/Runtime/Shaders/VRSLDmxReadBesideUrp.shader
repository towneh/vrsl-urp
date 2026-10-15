// Test-only, compiled and never drawn. The public DMX include in a shader that also uses
// URP's Lit inputs or its depth texture, in either order: what a world's own lit shader
// includes. One pass per pairing.
Shader "Hidden/VRSL-URP/Tests/DmxReadBesideUrp"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "LitThenDmx"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/VRSLDMX.hlsl"
            float4 Vert(uint id : SV_VertexID) : SV_POSITION { return float4(_Metallic + VRSLDMX_Channel(1u), 0, 0, 1); }
            float4 Frag(float4 p : SV_POSITION) : SV_Target  { return _BaseColor * VRSLDMX_Color(1u); }
            ENDHLSL
        }

        Pass
        {
            Name "DmxThenLit"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/VRSLDMX.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            float4 Vert(uint id : SV_VertexID) : SV_POSITION { return float4(_Metallic + VRSLDMX_Channel(1u), 0, 0, 1); }
            float4 Frag(float4 p : SV_POSITION) : SV_Target  { return _BaseColor * VRSLDMX_Color(1u); }
            ENDHLSL
        }

        Pass
        {
            Name "DepthThenDmx"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/VRSLDMX.hlsl"
            float4 Vert(uint id : SV_VertexID) : SV_POSITION { return float4(VRSLDMX_Strobe(7u), 0, 0, 1); }
            float4 Frag(float4 p : SV_POSITION) : SV_Target  { return SampleSceneDepth(float2(0.5, 0.5)) + VRSLDMX_ChannelRaw(1u); }
            ENDHLSL
        }

        Pass
        {
            Name "DmxThenDepth"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/VRSLDMX.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            float4 Vert(uint id : SV_VertexID) : SV_POSITION { return float4(VRSLDMX_Strobe(7u), 0, 0, 1); }
            float4 Frag(float4 p : SV_POSITION) : SV_Target  { return SampleSceneDepth(float2(0.5, 0.5)) + VRSLDMX_ChannelRaw(1u); }
            ENDHLSL
        }
    }
}
