Shader "Hidden/VRSL-URP/BasisVideoUVBlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        _UvBL ("UV Bottom-Left", Vector) = (0,0,0,0)
        _UvBR ("UV Bottom-Right", Vector) = (1,0,0,0)
        _UvTR ("UV Top-Right", Vector) = (1,1,0,0)
        _UvTL ("UV Top-Left", Vector) = (0,1,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always Lighting Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            sampler2D _MainTex;
            float4 _UvBL;
            float4 _UvBR;
            float4 _UvTR;
            float4 _UvTL;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // Bilinear remap of the destination [0,1] quad onto the four source UVs,
            // reproducing the per-corner GL quad (BL/BR/TR/TL at the dst corners) so
            // crop, rotation, flip and shear are all expressible.
            fixed4 frag (v2f i) : SV_Target
            {
                float2 bottom = lerp(_UvBL.xy, _UvBR.xy, i.uv.x);
                float2 top    = lerp(_UvTL.xy, _UvTR.xy, i.uv.x);
                return tex2D(_MainTex, lerp(bottom, top, i.uv.y));
            }
            ENDCG
        }
    }
}
