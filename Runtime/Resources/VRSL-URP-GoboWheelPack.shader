Shader "Hidden/VRSL-URP/GoboWheelPack"
{
    // Packs one gobo into a slice of the scene's gobo wheel (VRSLGoboWheel). Kept in
    // Resources because the wheel is built by the client's scripts whatever world is
    // loaded, so the shader has to be in the client's build rather than a world's.
    Properties
    {
        _MainTex ("Gobo", 2D) = "white" {}
        _VRSLGoboPack ("0 grey from red, 1 grey from alpha, 2 colour", Float) = 0
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
            float4 _MainTex_ST;
            float _VRSLGoboPack;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            // A grey gobo is stored as its mask in all three channels, so the
            // lighting's .rgb read multiplies by the same value in each and the
            // light keeps its colour. A colour gobo is stored premultiplied by its
            // alpha, so a logo drawn on transparency lands only where it's opaque.
            float4 frag (v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_VRSLGoboPack > 1.5) return float4(c.rgb * c.a, 1.0);
                float g = _VRSLGoboPack > 0.5 ? c.a : c.r;
                return float4(g, g, g, 1.0);
            }
            ENDCG
        }
    }
}
