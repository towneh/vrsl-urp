// Raymarched volumetric in-scattering for VRSL URP realtime lights.
// Runs immediately after VRSLDeferredLighting in the same per-camera schedule, reading the
// same _VRSLLights StructuredBuffer the surface pass produces. Three sub-passes:
//
//   Pass 0 — Depth Downsample. Full-res _CameraDepthTexture → half-res depth
//            (min-depth in linear, max raw value in reversed-Z) so the
//            raymarch terminates correctly at silhouettes.
//   Pass 1 — Raymarch. Half-res in-scattering accumulation along each pixel's
//            view ray; output is a half-res HDR colour buffer.
//   Pass 2 — Bilateral Upsample. Edge-aware reconstruction to full resolution,
//            additive blend onto the camera color target.
//
// The march is half-res and only half-res. The upsample is bilateral, so it
// rejects taps across a depth discontinuity and holds an edge that a trilinear
// one would smear — which is what makes the half-res march affordable rather
// than a compromise.
Shader "Hidden/VRSL-URP/VolumetricLighting"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSLLightingLibrary.hlsl"
            #include "Packages/town.mr.vrsl-urp/Runtime/Shaders/Shared/VRSLTileCulling.hlsl"

            // Mesh-driven attributes — manager renders RenderingUtils.fullscreenMesh,
            // whose vertices are already in clip space (-1..1). Each pass below also
            // declares the matching `multi_compile _ STEREO_INSTANCING_ON
            // STEREO_MULTIVIEW_ON` and runs UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX
            // in its frag(); URP's XR system applies SetInstanceMultiplier(viewCount)
            // so each per-eye instance writes to the correct slice via SV_RTAI.
            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionCS = float4(input.positionOS, 1.0);
                o.uv         = input.uv;
            #if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1.0 - o.uv.y;
            #endif
                return o;
            }

            SamplerState sampler_point_clamp;

            // ── Raymarch globals ─────────────────────────────────────────────
            StructuredBuffer<VRSLLightData> _VRSLLights;
            uint   _VRSLLightCount;

            // x = step count, y = couple-to-scene-fog flag (0/1),
            // w = HG anisotropy g
            float4 _VRSLVolStepCount;
            // x = base density, y = noise scale, z = noise scroll speed,
            // w = noise strength (modulated variant only)
            float4 _VRSLVolDensity;
            // xyz = colour tint, w = global intensity multiplier
            float4 _VRSLVolFogTint;

            // Interleaved gradient noise (Jimenez 2014), offsetting each pixel's
            // step phase along the ray.
            //
            // The dither has to decorrelate in both screen axes. A plain Weyl
            // lattice — frac(a*x + b*y) — does not: its iso-value contours are
            // straight lines of slope -a/b, so every pixel along that diagonal
            // receives the same phase and the raymarch's residual stepping
            // streaks into a visible diagonal weave over the cones. IGN's outer
            // multiply amplifies a very shallow inner gradient, so neighbours in
            // both axes land far apart in the output and it reads as fine grain.
            //
            // The frame offset decorrelates across frames so motion averages the
            // residual out. Any monotonic time term does that — the 60 is a
            // pseudo-frame rate for scaling, not an assumption about the real
            // frame rate.
            float VRSL_Jitter(float2 pixelCoord)
            {
                float2 p = pixelCoord + fmod(_Time.y * 60.0, 64.0) * 5.588238;
                return frac(52.9829189
                          * frac(dot(p, float2(0.06711056, 0.00583715))));
            }


            // Accumulate VRSL light in-scattering from the camera through the
            // pixel out to rawDepth. Returns RGB radiance with alpha = 0, so the
            // march can write into a fresh half-res target without disturbing
            // the destination alpha.
            float4 VRSL_Raymarch(float rawDepth, float2 uv, float2 pixelCS)
            {
            #if UNITY_REVERSED_Z
                if (rawDepth < 0.0001) return 0;   // skybox / far plane
            #else
                if (rawDepth > 0.9999) return 0;
            #endif

                float3 surfaceWS = ComputeWorldSpacePosition(
                    uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 cameraWS  = _WorldSpaceCameraPos.xyz;

                float3 viewDelta = surfaceWS - cameraWS;
                float  maxDist   = length(viewDelta);
                float3 viewDir   = viewDelta / max(maxDist, 0.0001);
                float3 toCamera  = -viewDir;

                int   stepCount = max(1, (int)_VRSLVolStepCount.x);

                float jitter   = VRSL_Jitter(pixelCS);
                float density  = _VRSLVolDensity.x;
                float3 tint    = _VRSLVolFogTint.xyz;
                float anisotropy = _VRSLVolStepCount.w;

                // Optional URP scene-fog coupling. unity_FogParams.x is the scene
                // fog coefficient (≈ density / sqrt(ln 2) for Exp2 mode); folding
                // it in lets a VolumeProfile globally drive shaft brightness, and
                // disabling fog suppresses the volumetric entirely.
                if (_VRSLVolStepCount.y > 0.5)
                {
                    density *= max(unity_FogParams.x, 0.0);
                    tint    *= unity_FogColor.rgb;
                }

            #ifdef _VRSL_VOLUMETRIC_NOISE
                float noiseScale    = _VRSLVolDensity.y;
                float noiseScroll   = _VRSLVolDensity.z;
                float noiseStrength = _VRSLVolDensity.w;
            #endif

                float3 accumulated = 0;

                // The whole view ray for this pixel stays inside one screen
                // tile, and the tile frustum spans the camera's full depth
                // range, so the light list is resolved once and reused for
                // every light marched below. That turns the outer loop from
                // "every fixture in the scene" into "the fixtures that reach
                // this tile".
                uint tileIndex  = VRSL_TileIndex(uv, VRSL_EyeIndex());
                uint lightCount = VRSL_LightListCount(tileIndex, _VRSLLightCount);

                // Each light is integrated only across the span of the view ray
                // that falls inside its own range, so sample density depends on
                // how thick the beam is rather than on how far away the surface
                // behind it happens to be. A shared march over the full ray
                // would put the whole budget into empty space whenever the
                // geometry behind a beam is distant, leaving the cone itself
                // with a sample or two; the jitter then turns that
                // undersampling into visible grain, which is what makes the
                // half-res path read as dithered.
                //
                // The per-light step budget is the full step count, so the
                // worst case costs what a shared march costs. Rays that miss a
                // light's sphere skip it outright.
                [loop]
                for (uint slot = 0; slot < lightCount; slot++)
                {
                    VRSLLightData light =
                        _VRSLLights[VRSL_LightListIndex(tileIndex, slot)];

                    if (!VRSL_IsActive(light)) continue;

                    // Ray against the light's bounding sphere, solved in march
                    // parameter t. perpSq is the squared distance from the centre
                    // to the ray; when it exceeds the range the ray misses and the
                    // light costs a handful of ALU instead of a full march.
                    float3 toCentre = light.positionAndRange.xyz - cameraWS;
                    float  range    = light.positionAndRange.w;
                    float  proj     = dot(toCentre, viewDir);
                    float  perpSq   = dot(toCentre, toCentre) - proj * proj;
                    float  halfSq   = range * range - perpSq;
                    if (halfSq <= 0.0) continue;

                    // Clamped to the visible ray, so a light sitting behind the
                    // camera or entirely beyond the opaque surface drops out
                    // before any stepping happens.
                    float halfChord = sqrt(halfSq);
                    float spanStart = max(proj - halfChord, 0.0);
                    float spanEnd   = min(proj + halfChord, maxDist);
                    if (spanEnd <= spanStart) continue;

                    // For a spot, tighten the sphere span to the cone itself.
                    // The sphere includes everything outside the beam and the
                    // whole backward hemisphere, so on a narrow cone it can be
                    // tens of times longer than the lit part of the ray. Point
                    // lights fill their sphere, so the sphere span is already
                    // tight for them.
                    if (VRSL_LightType(light) < 0.5)
                    {
                        // Same virtual apex VRSL_SpotAttenuation uses, so the
                        // span matches the cone the attenuation actually lights.
                        float3 apex = light.positionAndRange.xyz
                                    - light.directionAndType.xyz * light.spotParams.z;

                        if (!VRSL_NarrowSpanToCone(cameraWS, viewDir, apex,
                                                   light.directionAndType.xyz,
                                                   light.spotParams.y,
                                                   spanStart, spanEnd))
                            continue;
                    }

                    float stepSize = (spanEnd - spanStart) / stepCount;

                    [loop]
                    for (int s = 0; s < stepCount; s++)
                    {
                        float3 samplePos = cameraWS + viewDir
                                         * (spanStart + (s + jitter) * stepSize);

                        float3 contrib = VRSL_EvaluateLightVolumetric(
                            light, samplePos, toCamera, anisotropy);

                        // The gobo fetch is the most expensive part of the step,
                        // and a gobo can only reduce the result — skip the rest of
                        // the step wherever the light already contributes nothing.
                        if (!any(contrib > 0.0)) continue;

                        contrib *= SampleGobo(
                            VRSL_GoboIndex(light), light.spotParams.w,
                            samplePos,
                            light.positionAndRange.xyz,
                            light.directionAndType.xyz,
                            light.spotParams.y,
                            light.spotParams.z);

                    #ifdef _VRSL_VOLUMETRIC_NOISE
                        // Now evaluated per light rather than once per shared
                        // step, so haze varies with each beam's own sample
                        // positions. Costs a noise fetch per light per step where
                        // beams overlap; the keyword is off by default.
                        float3 noisePos = samplePos * noiseScale;
                        noisePos.y -= _Time.y * noiseScroll;
                        float n = VRSL_ValueNoise3D(noisePos);
                        contrib *= lerp(1.0, n, noiseStrength);
                    #endif

                        accumulated += contrib * density * stepSize;
                    }
                }

                float3 result = accumulated * tint * _VRSLVolFogTint.w;
                return float4(result, 0);
            }
        ENDHLSL

        // ── Pass 0 ───────────────────────────────────────────────────────────
        // Depth downsample: emit the depth that is closest to the camera within
        // each 2×2 source quad. In reversed-Z that is the maximum raw value.
        // Using min-depth keeps the raymarch tight to foreground silhouettes;
        // any half-res taps that lose background coverage are recovered by the
        // bilateral upsample in pass 2.
        Pass
        {
            Name "VRSL_Vol_DepthDownsample"
            Blend Off
            ZWrite Off
            ZTest  Off
            Cull   Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 ts = _CameraDepthTexture_TexelSize.xy;
                float d0 = SampleSceneDepth(i.uv);
                float d1 = SampleSceneDepth(i.uv + float2(ts.x, 0));
                float d2 = SampleSceneDepth(i.uv + float2(0, ts.y));
                float d3 = SampleSceneDepth(i.uv + float2(ts.x, ts.y));
            #if UNITY_REVERSED_Z
                float d = max(max(d0, d1), max(d2, d3));
            #else
                float d = min(min(d0, d1), min(d2, d3));
            #endif
                return float4(d, 0, 0, 0);
            }
            ENDHLSL
        }

        // ── Pass 1 ───────────────────────────────────────────────────────────
        // Raymarch the visible portion of each view ray, accumulating
        // in-scattering from every VRSL light. Reads the half-res depth from
        // pass 0; emits a half-res HDR colour buffer that pass 2 composites.
        Pass
        {
            Name "VRSL_Vol_Raymarch"
            Blend Off
            ZWrite Off
            ZTest  Off
            Cull   Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5
            #pragma multi_compile _ _VRSL_VOLUMETRIC_NOISE
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            // TEXTURE2D_X resolves to Texture2DArray under SPI VR (manager
            // allocates the half-res depth as an array, one slice per eye)
            // and to Texture2D otherwise. SAMPLE_TEXTURE2D_X_LOD picks the
            // right slice from unity_StereoEyeIndex automatically.
            TEXTURE2D_X(_VRSLVolHalfResDepth);

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float rawDepth = SAMPLE_TEXTURE2D_X_LOD(
                    _VRSLVolHalfResDepth, sampler_point_clamp, i.uv, 0).r;
                return VRSL_Raymarch(rawDepth, i.uv, i.positionCS.xy);
            }
            ENDHLSL
        }

        // ── Pass 2 ───────────────────────────────────────────────────────────
        // Bilateral upsample composite. For each full-res pixel: sample a 3×3
        // neighbourhood of half-res taps, weight Gaussian × exp(-|depthDiff|),
        // and add the weighted-average to the camera colour. The 9-tap footprint
        // doubles as a low-pass filter that smooths the half-res raymarch
        // jitter pattern; the bilateral term keeps foreground silhouettes from
        // fringe-bleeding half-res values sampled from background neighbours.
        Pass
        {
            Name "VRSL_Vol_Upsample"
            Blend One One
            ColorMask RGB   // additive light only — never disturb scene alpha
            ZWrite Off
            ZTest  Off
            Cull   Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            // TEXTURE2D_X handles both single-slice (desktop) and per-eye
            // Texture2DArray (SPI VR) — manager allocates the dimension to
            // match. SAMPLE_TEXTURE2D_X_LOD picks the slice from
            // unity_StereoEyeIndex.
            TEXTURE2D_X(_VRSLVolumetricRT);
            TEXTURE2D_X(_VRSLVolHalfResDepth);

            // xy = (halfW, halfH), zw = (1/halfW, 1/halfH). Set by the manager
            // before DrawMesh; replaces a Texture2D.GetDimensions call which
            // doesn't have a clean form on TEXTURE2D_X.
            float4 _VRSLVolHalfResSize;

            float4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float fullDepth = SampleSceneDepth(i.uv);
            #if UNITY_REVERSED_Z
                if (fullDepth < 0.0001) return 0;
            #else
                if (fullDepth > 0.9999) return 0;
            #endif

                float2 halfRes   = _VRSLVolHalfResSize.xy;
                float2 halfTexel = _VRSLVolHalfResSize.zw;

                // This pixel's position in half-res texel space, split into the
                // nearest texel and the signed offset within it. The sub-texel
                // part has to drive the weights: snapping every full-res pixel to
                // its texel centre gives all four pixels of a 2×2 block the same
                // taps and the same weights, so the composite is constant across
                // the block. On a gradient as smooth as a beam that terraces into
                // visible contours however good the depth rejection is.
                float2 halfPos  = i.uv * halfRes - 0.5;
                float2 ctrTexel = round(halfPos);
                float2 subTexel = halfPos - ctrTexel;   // [-0.5, 0.5]

                const float2 offs[9] = {
                    float2(-1,-1), float2(0,-1), float2(1,-1),
                    float2(-1, 0), float2(0, 0), float2(1, 0),
                    float2(-1, 1), float2(0, 1), float2(1, 1)
                };

                float fullEye = LinearEyeDepth(fullDepth, _ZBufferParams);

                // Depth rejection is measured against a tolerance proportional to
                // viewing distance, so the term means the same thing near and far.
                // The tolerance has to be a real distance rather than a guard
                // epsilon: weighting by 1/depthDiff alone is unbounded as the
                // difference approaches zero, which hands the centre tap a weight
                // orders of magnitude above its neighbours on any surface that
                // isn't exactly fronto-parallel. That collapses the kernel to a
                // point sample, and a point-sampled half-res buffer replicated to
                // full res is what turns the raymarch jitter into visible blocky
                // structure instead of the grain it was shaped to be.
                float tolerance = max(fullEye * 0.02, 0.02);

                float4 sum  = 0;
                float  wSum = 0;

                [unroll]
                for (int j = 0; j < 9; j++)
                {
                    float2 uv = (ctrTexel + offs[j] + 0.5) * halfTexel;
                    float halfDepth = SAMPLE_TEXTURE2D_X_LOD(
                        _VRSLVolHalfResDepth, sampler_point_clamp, uv, 0).r;
                    float halfEye  = LinearEyeDepth(halfDepth, _ZBufferParams);

                    // Separable tent centred on the true sub-texel position, so
                    // weights slide continuously as the pixel crosses the block
                    // instead of stepping at texel boundaries. Radius 1.5 is what
                    // makes that continuous: the tap that leaves the footprint as
                    // ctrTexel flips has already fallen to zero weight.
                    float2 d       = abs(offs[j] - subTexel);
                    float  spatial = max(0.0, 1.5 - d.x) * max(0.0, 1.5 - d.y);

                    // Flat within tolerance keeps the full spatial weight; a
                    // silhouette falls off quadratically and still gets rejected.
                    float depthDiff = abs(fullEye - halfEye) / tolerance;
                    float bilateral = rcp(1.0 + depthDiff * depthDiff);
                    float w = spatial * bilateral;
                    sum  += SAMPLE_TEXTURE2D_X_LOD(
                        _VRSLVolumetricRT, sampler_point_clamp, uv, 0) * w;
                    wSum += w;
                }

                return sum / max(wSum, 0.0001);
            }
            ENDHLSL
        }
    }
}
