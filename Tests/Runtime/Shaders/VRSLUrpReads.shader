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

            // Pixel x reads channel x + 1 + this, so -1 starts at channel 0.
            int _ProbeChannelOffset;

            float4 Vert(uint id : SV_VertexID) : SV_POSITION
            {
                return GetFullScreenTriangleVertexPosition(id);
            }

            float4 Frag(float4 position : SV_POSITION) : SV_Target
            {
                uint ch = (uint) ((int) position.x + 1 + _ProbeChannelOffset);
                return float4(DMXValue(ch), DMXMovement(ch), DMXSpinPhase(ch), DMXStrobe(ch));
            }
            ENDHLSL
        }
    }
}
