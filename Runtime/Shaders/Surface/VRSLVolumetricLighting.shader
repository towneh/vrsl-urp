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

            // x = max steps per light, y = couple-to-scene-fog flag (0/1),
            // z = 1 / sample spacing in metres, w = HG anisotropy g
            float4 _VRSLVolStepCount;

            // Seconds since level load, from the CPU rather than _Time.y, so a
            // capture that holds everything else still can hold the dither and
            // the haze scroll still as well. Both are meant to move between
            // frames; a frozen frame is the one case where they must not.
            float _VRSLVolTime;

            // Diagnostic counters, written only while _VRSLVolCollectStats is set:
            // one atomic per counter per pixel at the end of the march, so a
            // frame that collects is not a frame anyone should time. Slots are
            // pixels marched, lights marched, steps taken, lights skipped by the
            // visibility bound. Always bound by the pass, collecting or not.
            RWStructuredBuffer<uint> _VRSLVolStats : register(u1);
            int _VRSLVolCollectStats;

            // Fewest steps a light is marched with, however short its span. One
            // or two samples across a short span alias into dots that swim as
            // the camera moves, which is worse than the cost they save.
            #define VRSL_MIN_VOL_STEPS 4

            // The least a light may add to a pixel and still be marched, in the
            // units the pixel is written in: linear radiance after density, span,
            // tint and the global intensity have all been applied, which is what
            // lands in the frame. 1/4096 is under one 8-bit step at black, so a
            // light whose whole span cannot reach it cannot be seen against any
            // background. Relative to what is written rather than to a decoded
            // intensity, so it holds whatever range a rig's intensities run to.
            // Zero switches the test off without touching anything else.
            #define VRSL_VOL_MIN_CONTRIB (1.0 / 4096.0)
            // x = base density, y = noise scale, z = noise scroll speed,
            // w = noise strength (modulated variant only)
            float4 _VRSLVolDensity;
            // xyz = colour tint, w = global intensity multiplier
            float4 _VRSLVolFogTint;

            // The density field, baked by the manager from the library's own
            // noise function (see VRSL_ValueNoise3DPeriodic and the constants
            // beside it). One trilinear fetch per light per step, in place of
            // the eight hash taps the procedural form costs. The sampler wraps,
            // which is what makes a texture holding one period tile the world.
            Texture3D<float> _VRSLVolNoise;
            SamplerState     sampler_linear_repeat;

            float VRSL_VolumetricNoise(float3 posWS, float scale, float scroll, float time)
            {
                float3 p = posWS * scale;
                p.y -= time * scroll;
                return _VRSLVolNoise.SampleLevel(sampler_linear_repeat,
                                                 p * VRSL_VOL_NOISE_INV_PERIOD, 0);
            }

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
                float2 p = pixelCoord + fmod(_VRSLVolTime * 60.0, 64.0) * 5.588238;
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

                // Ceiling on the steps one light may take. The count itself is
                // set per light from its span, below.
                int   maxSteps   = max(VRSL_MIN_VOL_STEPS, (int)_VRSLVolStepCount.x);
                float invSpacing = _VRSLVolStepCount.z;

                float jitter   = VRSL_Jitter(pixelCS);
                float density  = _VRSLVolDensity.x;
                float3 tint    = _VRSLVolFogTint.xyz;
                float anisotropy = _VRSLVolStepCount.w;
                float maxPhase   = VRSL_MaxPhase(anisotropy);

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

                // Everything a light's accumulated result is scaled by after the
                // per-sample terms, folded once so the bound below compares in
                // the units the pixel is written in. The brightest channel
                // rather than luminance: a light that is all blue must not be
                // bounded by how little blue weighs.
                float outputScale = density * _VRSLVolFogTint.w
                                  * max(tint.r, max(tint.g, tint.b));

                float3 accumulated = 0;
                uint   marched     = 0;
                uint   stepsTaken  = 0;
                uint   skipped     = 0;

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
                // Each light's step count follows its span at a constant world
                // spacing and is capped at the level's maximum, so the worst
                // case costs what a shared march costs and the common case
                // costs far less. Rays that miss a light's sphere skip it
                // outright.
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
                    // A discoball draws in the haze only when the fixture asks: its
                    // dots are hundreds of thin beams, a cubemap fetch per step for
                    // one light across every tile its sphere covers.
                    if (VRSL_IsDiscoball(light) && light.spotParams.z < 0.5) continue;

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

                    float span = spanEnd - spanStart;

                    // Bound the most this light can add across the whole span
                    // and skip it when that cannot be seen. Evaluated at the
                    // ray's closest approach to the light within the span, which
                    // is where distance attenuation peaks; the angular falloff,
                    // the gobo, the phase function and the noise can each only
                    // reduce a sample from there, so every step is at or below
                    // this and the sum over the span is at or below peak times
                    // span. Conservative by construction: it marches lights it
                    // need not, and never skips one that would have shown.
                    if (VRSL_VOL_MIN_CONTRIB > 0.0)
                    {
                        float  tClosest = clamp(proj, spanStart, spanEnd);
                        float3 toLightC = toCentre - viewDir * tClosest;
                        float3 colour   = light.colorAndIntensity.xyz;
                        float  peak     = VRSL_DistanceAttenuation(dot(toLightC, toLightC), range)
                                        * light.colorAndIntensity.w
                                        * max(colour.r, max(colour.g, colour.b))
                                        * maxPhase;
                        if (peak * span * outputScale < VRSL_VOL_MIN_CONTRIB)
                        {
                            skipped += 1;
                            continue;
                        }
                    }

                    // Steps from the span at a constant spacing, so a cone
                    // clipping half a metre of the ray costs a fraction of one
                    // running thirty metres down it. The level sets the spacing
                    // and the ceiling: a long span at High may take more steps
                    // than the same span at Standard, which is the intent.
                    int   steps    = clamp((int)ceil(span * invSpacing),
                                           VRSL_MIN_VOL_STEPS, maxSteps);
                    float stepSize = span / steps;
                    marched    += 1;
                    stepsTaken += (uint)steps;

                    [loop]
                    for (int s = 0; s < steps; s++)
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
                            light.spotParams.z)
                                 * VRSL_DiscoballMask(light, samplePos);

                    #ifdef _VRSL_VOLUMETRIC_NOISE
                        // Evaluated per light rather than once per shared step,
                        // so haze varies with each beam's own sample positions.
                        // Costs a texture fetch per light per step where beams
                        // overlap.
                        float n = VRSL_VolumetricNoise(samplePos, noiseScale,
                                                       noiseScroll, _VRSLVolTime);
                        contrib *= lerp(1.0, n, noiseStrength);
                    #endif

                        accumulated += contrib * density * stepSize;
                    }
                }

                if (_VRSLVolCollectStats != 0)
                {
                    InterlockedAdd(_VRSLVolStats[0], 1u);
                    InterlockedAdd(_VRSLVolStats[1], marched);
                    InterlockedAdd(_VRSLVolStats[2], stepsTaken);
                    InterlockedAdd(_VRSLVolStats[3], skipped);
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
