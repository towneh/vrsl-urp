// Material-property capture for the VRSL URP surface prepass.
//
// Rendered through DrawingSettings.overrideShader, so every opaque renderer is
// drawn with this shader but keeps its own material's property values. That is
// what lets the lighting pass reach albedo on shaders VRSL knows nothing about
// — URP Lit, Poiyomi URP, lilToon URP, Mochie URP — without asking authors to
// add a VRSL-specific pass.
//
//   SV_Target0 (_VRSLAlbedoTexture)   rgb = base colour, a = smoothness
//   SV_Target1 (_VRSLMaterialTexture) r   = metallic
//
// Pass 0 draws the plain opaque queue; pass 1 draws the alpha-test queue and
// clips on _Cutoff. The manager splits the renderer list by queue range so an
// opaque material whose base map stores non-colour data in alpha is never
// clipped against a stale _Cutoff.
Shader "Hidden/VRSL-URP/SurfaceProperties"
{
    Properties
    {
        // Both naming conventions are declared because the override shader has
        // to serve URP-native materials (_BaseMap / _BaseColor) and the avatar
        // shaders social-VR scenes are full of (_MainTex / _Color). A property
        // the material doesn't carry resolves to the default given here, and
        // the fragment combines the two pairs with min() — see below.
        _BaseMap    ("Base Map (URP)",            2D)    = "white" {}
        _BaseColor  ("Base Color (URP)",          Color) = (1,1,1,1)
        _MainTex    ("Main Tex (legacy/avatar)",  2D)    = "white" {}
        _Color      ("Color (legacy/avatar)",     Color) = (1,1,1,1)
        _Smoothness ("Smoothness (URP)",          Float) = 0
        _Glossiness ("Smoothness (legacy)",       Float) = 0
        _Metallic   ("Metallic",                  Float) = 0
        _Cutoff     ("Alpha Cutoff",              Float) = 0.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        // For VRSL_SURFACE_DEPTH_TOLERANCE, shared with VRSL_SurfaceDataCovers so
        // the prepass gate and the lighting-pass backstop agree.
        #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl"

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _MainTex_ST;
            half4  _BaseColor;
            half4  _Color;
            half   _Smoothness;
            half   _Glossiness;
            half   _Metallic;
            half   _Cutoff;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uvBase     : TEXCOORD0;
            float2 uvMain     : TEXCOORD1;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings vert(Attributes input)
        {
            Varyings o = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            o.uvBase     = TRANSFORM_TEX(input.uv, _BaseMap);
            o.uvMain     = TRANSFORM_TEX(input.uv, _MainTex);
            return o;
        }

        // Resolve base colour from whichever naming convention the material uses.
        //
        // A tint alpha of zero marks a property the material does not declare:
        // an unbound scalar resolves to zero rather than to the default above,
        // and a fully transparent tint is meaningless on an opaque renderer.
        // Folding that pair to white keeps it out of the min() below. Texture
        // slots need no such guard — Unity binds the default texture named in
        // Properties when a material has no texture for the slot.
        //
        // min() rather than a product: a URP material leaves the legacy pair at
        // white so the URP pair wins; an avatar material leaves the URP pair at
        // white so the legacy pair wins; and a material converted from Standard
        // that still carries a stale _MainTex pointing at the same texture
        // resolves to that texture once instead of squaring it. Where a material
        // genuinely populates both with different values the darker wins, which
        // under-applies light rather than blowing it out.
        half4 SampleBaseColor(Varyings i)
        {
            half4 urp    = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uvBase) * _BaseColor;
            half4 legacy = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uvMain) * _Color;

            if (_BaseColor.a <= 0) urp    = half4(1, 1, 1, 1);
            if (_Color.a    <= 0) legacy = half4(1, 1, 1, 1);

            return min(urp, legacy);
        }

        // Discard fragments the camera didn't keep.
        //
        // This pass draws through DrawingSettings.overrideShader, which replaces
        // the material's own shader outright. That is what reaches albedo on
        // shaders VRSL knows nothing about, but it also means any visibility
        // decision living inside that shader never runs here — Poiyomi's UV Tile
        // Discard, custom alpha clips, vertex displacement. Left alone, this pass
        // draws geometry the camera dropped and overwrites the albedo of whatever
        // is genuinely visible behind it.
        //
        // Testing against the camera's depth restores those decisions without
        // needing to know any individual shader's rule. Rejecting here rather
        // than filtering the result downstream is the part that matters: once a
        // hidden surface has written albedo, the surface behind it is gone and no
        // later check can recover it — the best a downstream filter can do is
        // substitute a neutral value, which still leaves the hidden shape legible
        // wherever it differs from its surroundings.
        //
        // A tolerance in linear eye space rather than equality, because the two
        // depths come from separate shader compilations of the same transform and
        // so agree closely rather than bit-exactly.
        //
        // It has to be tight. Both sides describe the same vertex, so honest
        // disagreement is float precision — far below a micrometre against a
        // 32-bit reversed-Z buffer at any distance an avatar is viewed from. What
        // the test has to separate is a garment from the skin beneath it, which on
        // a fitted mesh is a couple of millimetres. A tolerance anywhere near that
        // gap leaves the comparison marginal across whole surfaces, which shows up
        // as a stipple of clipped and unclipped pixels wherever a light lands.
        // 0 when the prepass could not get hold of the camera depth texture, in
        // which case the comparison below would run against an unbound texture and
        // reject everything. Drawing unfiltered is the milder failure.
        float _VRSLSurfaceDepthGate;

        void ClipToCameraDepth(float4 positionCS)
        {
            if (_VRSLSurfaceDepthGate < 0.5) return;

            float2 screenUV = positionCS.xy / _ScreenParams.xy;

            float cameraEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
            float fragEye   = LinearEyeDepth(positionCS.z,               _ZBufferParams);

            clip(VRSL_SURFACE_DEPTH_TOLERANCE(cameraEye) - abs(fragEye - cameraEye));
        }

        void WriteSurface(half4 baseColor,
                          out half4 outAlbedo, out half4 outMaterial)
        {
            // Both smoothness names default to 0 here, so max() picks whichever
            // one the material actually declares.
            half smoothness = max(_Smoothness, _Glossiness);
            outAlbedo   = half4(baseColor.rgb, smoothness);
            outMaterial = half4(_Metallic, 0, 0, 0);
        }
        ENDHLSL

        // ── Pass 0 — opaque queue (2000–2449), no alpha clip ─────────────────
        Pass
        {
            Name "VRSL_SurfaceProperties"
            ZWrite On
            ZTest  LEqual
            Cull   Back

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5
            // Only the explicit stereo form, never multi_compile_instancing
            // alongside it: in a URP HLSLPROGRAM the pair expands to a cartesian
            // variant matrix that Unity can resolve to INSTANCING_ON and
            // STEREO_INSTANCING_ON at once, which renders the geometry into
            // neither eye. Losing the GPU-instancing variant costs some batching
            // on this prepass; drawing nothing costs the whole feature.
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            void frag(Varyings i, out half4 outAlbedo : SV_Target0,
                                  out half4 outMaterial : SV_Target1)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                ClipToCameraDepth(i.positionCS);
                WriteSurface(SampleBaseColor(i), outAlbedo, outMaterial);
            }
            ENDHLSL
        }

        // ── Pass 1 — alpha-test queue (2450–2500), clips on _Cutoff ──────────
        Pass
        {
            Name "VRSL_SurfacePropertiesAlphaTest"
            ZWrite On
            ZTest  LEqual
            Cull   Back

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5
            // Only the explicit stereo form, never multi_compile_instancing
            // alongside it: in a URP HLSLPROGRAM the pair expands to a cartesian
            // variant matrix that Unity can resolve to INSTANCING_ON and
            // STEREO_INSTANCING_ON at once, which renders the geometry into
            // neither eye. Losing the GPU-instancing variant costs some batching
            // on this prepass; drawing nothing costs the whole feature.
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            void frag(Varyings i, out half4 outAlbedo : SV_Target0,
                                  out half4 outMaterial : SV_Target1)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                ClipToCameraDepth(i.positionCS);

                half4 baseColor = SampleBaseColor(i);
                clip(baseColor.a - _Cutoff);
                WriteSurface(baseColor, outAlbedo, outMaterial);
            }
            ENDHLSL
        }
    }
}
