// Test-only. Finds the grid texel the fixtures' include reads each channel from, so a
// row can paint a grid whose every cell is known without deriving the layout itself.
//
// Drawn into a target one texel high with a texel per channel. Each texel samples an
// index texture, whose every texel holds its own coordinates, at the position the
// include's getDMXCellUV gives that channel. What comes back is the texel the sampler
// picked, which settles positions that fall exactly on a texel edge the way the
// hardware settles them rather than the way a re-derivation would guess.
Shader "Hidden/VRSL-URP/Tests/GridCells"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "Cells"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            // Color.hlsl for Luminance, which the include uses and the fixture shaders
            // bring in themselves.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSL-DMXFunctions-URP.hlsl"

            TEXTURE2D(_VRSLTestIndex);

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                // The target is one row, so only x matters, and x is never flipped.
                uint ch = (uint)i.positionCS.x + 1u;
                return SAMPLE_TEXTURE2D_LOD(_VRSLTestIndex, vrsldmx_point_clamp_sampler, getDMXCellUV(ch), 0);
            }
            ENDHLSL
        }
    }
}
