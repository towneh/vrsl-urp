// Test-only. A shader of a world's own, reading DMX through the public include and nothing
// else of the package's. Drawn into a target one texel high, a texel per channel: from the
// fragment stage over the whole target, or from the vertex stage as a point per channel.
// Each texel holds the reads offset to 0.25..0.75, so a texel nothing wrote reads below.
Shader "Hidden/VRSL-URP/Tests/DmxRead"
{
    Properties
    {
        _Probe ("Read (0 values, 1 colour), stage (0 fragment, 1 vertex), first channel", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/VRSLDMX.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Probe;
            CBUFFER_END

            struct Varyings
            {
                float4 position : SV_POSITION;
                nointerpolation float4 reads : TEXCOORD0;
            };

            // Channel, channel raw, strobe; or the colour of a fixture starting there.
            float4 Read(uint channel)
            {
                float4 reads;
                if (_Probe.x > 0.5) reads = float4(VRSLDMX_Color(channel).rgb, 1.0);
                else                reads = float4(VRSLDMX_Channel(channel), VRSLDMX_ChannelRaw(channel), VRSLDMX_Strobe(channel), 1.0);
                return reads;
            }

            float4 Encode(float4 reads)
            {
                return float4(reads.rgb * 0.5 + 0.25, 1.0);
            }

            Varyings Vert(uint id : SV_VertexID)
            {
                Varyings o;
                if (_Probe.y > 0.5)
                {
                    o.position = float4(((id + 0.5) / _ScreenParams.x) * 2.0 - 1.0, 0.0, 0.5, 1.0);
                    o.reads    = Encode(Read(id + (uint) _Probe.z));
                }
                else
                {
                    o.position = GetFullScreenTriangleVertexPosition(id);
                    o.reads    = 0;
                }
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                if (_Probe.y > 0.5) return i.reads;
                return Encode(Read((uint) i.position.x + (uint) _Probe.z));
            }
            ENDHLSL
        }
    }
}
