// Test-only. The pan and tilt angles the URP include's mover reads return for one channel,
// pan in red and tilt in green, in degrees.
Shader "Hidden/VRSL-URP/Tests/UrpPanTilt"
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
            #define FIXTURE_EMIT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSL-DMXFunctions-URP.hlsl"

            uint _ProbeChannel;

            float4 Vert(uint id : SV_VertexID) : SV_POSITION
            {
                return GetFullScreenTriangleVertexPosition(id);
            }

            float4 Frag(float4 position : SV_POSITION) : SV_Target
            {
                return float4(GetPanValue(_ProbeChannel), GetTiltValue(_ProbeChannel), 0.0, 1.0);
            }
            ENDHLSL
        }
    }
}
