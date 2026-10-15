// Test-only. What the Built-in read functions return for each channel: drawn into a
// target one texel high with a texel per channel, the value in red, the movement value
// in green, the spin phase in blue and the strobe (1 open) in alpha, from whichever
// source the bound properties say is live.
Shader "Hidden/VRSL-URP/Tests/BuiltInReads"
{
    SubShader
    {
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target 4.5
            #define VRSL_DMX
            #include "UnityCG.cginc"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSL-Defines.cginc"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSL-DMXFunctions.cginc"

            // Pixel x reads channel x + 1 + this, so -1 starts at channel 0.
            int _ProbeChannelOffset;

            float4 Vert(uint id : SV_VertexID) : SV_POSITION
            {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * 2.0 - 1.0, 0.0, 1.0);
            }

            float4 Frag(float4 position : SV_POSITION) : SV_Target
            {
                uint ch = (uint) ((int) position.x + 1 + _ProbeChannelOffset);
                return float4(DMXValue(ch), DMXMovement(ch), DMXSpinPhase(ch), DMXStrobe(ch));
            }
            ENDCG
        }
    }
}
