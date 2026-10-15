// Test-only. A world's stencil, as O5 and O6 need it: the writer material (the
// defaults) puts 142 into the stencil wherever it draws and touches neither
// colour nor depth, early in the opaque queue as upstream VRSL's light stencil
// did. The probe material (comparison Equal, keep, colour on, a late queue)
// draws flat green only where the stencil already holds 142, which is how a row
// knows the writer reached the frame it is judging.
Shader "Hidden/VRSL-URP/Tests/StencilProbe"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil Comparison", Float) = 8
        [Enum(UnityEngine.Rendering.StencilOp)] _StencilPass ("Stencil Pass", Float) = 2
        _ColorMask ("Colour Mask", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry-1000" "RenderType" = "Opaque" }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            ZTest Always
            Cull Off
            ColorMask [_ColorMask]
            Stencil
            {
                Ref 142
                Comp [_StencilComp]
                Pass [_StencilPass]
            }

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target { return half4(0, 1, 0, 1); }
            ENDHLSL
        }
    }
}
