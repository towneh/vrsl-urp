// Test-only. VRSLBuiltInReads for the URP read functions.
Shader "Hidden/VRSL-URP/Tests/UrpReads"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSL-DMXFunctions-URP.hlsl"

            float4 Vert(uint id : SV_VertexID) : SV_POSITION
            {
                return GetFullScreenTriangleVertexPosition(id);
            }

            float4 Frag(float4 position : SV_POSITION) : SV_Target
            {
                uint ch = (uint) position.x + 1u;
                return float4(DMXValue(ch), DMXMovement(ch), DMXSpinPhase(ch), 1.0);
            }
            ENDHLSL
        }
    }
}
