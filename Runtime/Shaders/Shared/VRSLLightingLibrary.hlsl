#ifndef VRSL_LIGHTING_LIBRARY_INCLUDED
#define VRSL_LIGHTING_LIBRARY_INCLUDED

#define VRSL_PI 3.14159265359

// How far apart two linear eye depths may be and still be taken to describe the
// same surface. Used to reconcile the surface prepass, which draws through an
// override shader, with the depth the camera actually kept — see
// VRSLSurfaceProperties and VRSL_SurfaceDataCovers.
//
// Both sides describe the same vertex, so honest disagreement is float precision,
// far below a micrometre against a 32-bit reversed-Z buffer at any distance an
// avatar is viewed from. What the comparison has to separate is a garment from
// the skin beneath it — a couple of millimetres on a fitted mesh. Set anywhere
// near that gap the test goes marginal across whole surfaces and resolves as a
// stipple of clipped and unclipped pixels, visible wherever a light lands.
#define VRSL_SURFACE_DEPTH_TOLERANCE(eyeDepth) max((eyeDepth) * 0.0005, 0.0005)

// ──────────────────────────────────────────────────────────────────────────────
// GPU data layout — must exactly match the C# structs in VRSL_DMXURPLightManager.cs
// ──────────────────────────────────────────────────────────────────────────────

// Per-fixture static configuration (CPU → GPU once, or on change)
struct VRSLFixtureConfig
{
    float4 positionAndRange;    // xyz = world position,  w = attenuation range
    float4 forwardAndType;      // xyz = base forward dir, w = light type (0=spot, 1=point,
                                //       2=discoball: xyz is then the spin axis)
    float4 rightAndMaxIntensity;// xyz = local +X in world space (tilt rotation axis), w = max intensity scalar
    float4 spotAngles;          // x = inner-to-outer ratio (0..1), y = max outer half-angle (deg),
                                // z = finalIntensity cap,          w = min outer half-angle (deg)
    float4 dmxChannel;          // x = absolute DMX channel, y = enableStrobe,
                                // z = enablePanTilt, w = enableFineChannels
    float4 panSettings;         // x = maxMinPan (deg), y = panOffset (deg),
                                // z = invertPan (0/1), w = enableGoboSpin (0/1)
    float4 tiltSettings;        // x = maxMinTilt (deg), y = tiltOffset (deg),
                                // z = invertTilt (0/1), w = enableGobo (0/1)
    float4 extras;              // x = emitterDepth (m), y = 5-channel mode flag,
                                // z = curveMod (body-glow dimmer-response match),
                                // w = discoball beams (1 = the raymarch draws its dots)
    float4 tintAndSpin;         // xyz = discoball colour (linear), w = spin (deg/s)
    float4 zone;                // x = zone slot + 1 (0 = none), yzw unused
};

// Per-fixture light state computed by the compute shader every frame.
// 64 bytes, 4 × float4. Read through the accessors below rather than by field,
// so the two packed slots have one definition.
struct VRSLLightData
{
    float4 positionAndRange;    // xyz = world position, w = range
    float4 directionAndType;    // xyz = normalised direction (spot) or spin axis (discoball),
                                // w   = light type and gobo slice, packed (see below)
    float4 colorAndIntensity;   // xyz = linear RGB, w = intensity (0 = inactive, skip)
    float4 spotParams;          // x = cos(inner half-angle), y = cos(outer half-angle),
                                // z = emitterDepth (m) — virtual cone-apex pushback,
                                //     or, for a discoball, 1 when the raymarch draws it
                                // w = spin phase (radians, wrapped to ±2π): the gobo's
                                //     for a spot, the ball's for a discoball
};

// directionAndType.w carries three integers in one float: the light type in the
// low two bits, the gobo slice above it, biased by one so "no gobo" (-1)
// survives, and from VRSL_ZONE_UNIT up the light's zone slot plus one, 0 for an
// unzoned light. All are small integers and floats represent those exactly to
// 2^24, so this costs no precision — unlike packing them as halves, which would
// quantise the spin phase and stipple a slowly rotating gobo. An unzoned light's
// value is below VRSL_ZONE_UNIT, so telling the two apart is one compare.
#define VRSL_ZONE_UNIT 1024.0

float VRSL_PackTypeGoboZone(float lightType, int goboIndex, float zoneCode)
{
    return lightType + (float)(min(goboIndex, 254) + 1) * 4.0 + zoneCode * VRSL_ZONE_UNIT;
}

// 0 = spot, 1 = point, 2 = discoball. Anything but a spot is omnidirectional.
float VRSL_LightType(VRSLLightData light)
{
    return fmod(light.directionAndType.w, 4.0);
}

bool VRSL_IsDiscoball(VRSLLightData light)
{
    return VRSL_LightType(light) > 1.5;
}

// -1 = no gobo, 0+ = slice in _VRSLGobos. For a zoned light, read after
// VRSL_TakeZone has taken the zone off.
float VRSL_GoboIndex(VRSLLightData light)
{
    return floor(light.directionAndType.w * 0.25) - 1.0;
}

// An inactive fixture emits nothing, so intensity doubles as the active flag and
// no separate slot is needed for it.
bool VRSL_IsActive(VRSLLightData light)
{
    return light.colorAndIntensity.w > 0.0;
}

// ──────────────────────────────────────────────────────────────────────────────
// Light zones — where a zoned light may reach (VRSLLightZones.cs)
// ──────────────────────────────────────────────────────────────────────────────

// One header per zone slot, (first box row, box count, 0, 0), then three rows
// per box: its world-to-box transform. A point is inside a box when its image
// lies in the unit cube centred on the origin, and inside the zone when it is
// inside any of its boxes. Bound globally by VRSLLightZones, the fallback buffer
// when no zone is on.
StructuredBuffer<float4> _VRSLZones;

bool VRSL_IsZoned(VRSLLightData light)
{
    return light.directionAndType.w >= VRSL_ZONE_UNIT;
}

// The zone slot a zoned light names, taken off its packed value so the gobo
// reads as it would unzoned.
uint VRSL_TakeZone(inout VRSLLightData light)
{
    float code = floor(light.directionAndType.w * (1.0 / VRSL_ZONE_UNIT));
    light.directionAndType.w -= code * VRSL_ZONE_UNIT;
    return (uint)code - 1u;
}

float3 VRSL_ToZoneBox(uint row, float3 posWS)
{
    float4 p = float4(posWS, 1.0);
    return float3(dot(_VRSLZones[row], p), dot(_VRSLZones[row + 1u], p), dot(_VRSLZones[row + 2u], p));
}

bool VRSL_InZoneBoxes(uint first, uint count, float3 posWS)
{
    bool inside = false;
    [loop]
    for (uint b = 0u; b < count; b++)
    {
        if (all(abs(VRSL_ToZoneBox(first + 3u * b, posWS)) <= 0.5))
        {
            inside = true;
            break;
        }
    }
    return inside;
}

// 1 where a zoned light may light posWS, 0 outside its zone. Takes the zone off
// the light, so call it only for a light VRSL_IsZoned says is zoned.
float VRSL_ZoneMask(inout VRSLLightData light, float3 posWS)
{
    float4 zone = _VRSLZones[VRSL_TakeZone(light)];
    return VRSL_InZoneBoxes((uint)zone.x, (uint)zone.y, posWS) ? 1.0 : 0.0;
}

// Where a ray runs through the zone box at row, from enter to leave in ray
// parameter t. False when it misses the box.
bool VRSL_ZoneBoxSpan(uint row, float3 rayOrigin, float3 rayDir,
                      out float enter, out float leave)
{
    float3 o   = VRSL_ToZoneBox(row, rayOrigin);
    float3 d   = float3(dot(_VRSLZones[row].xyz, rayDir),
                        dot(_VRSLZones[row + 1u].xyz, rayDir),
                        dot(_VRSLZones[row + 2u].xyz, rayDir));
    // Slabs of the unit cube; a ray parallel to a pair divides by zero, and
    // the infinities it makes fall out of the min and max.
    float3 inv = 1.0 / d;
    float3 t0  = (-0.5 - o) * inv;
    float3 t1  = ( 0.5 - o) * inv;
    float3 lo  = min(t0, t1);
    float3 hi  = max(t0, t1);
    enter = max(max(lo.x, lo.y), lo.z);
    leave = min(min(hi.x, hi.y), hi.z);
    return enter < leave;
}

// The next stretch of a ray inside a zone's boxes at or after t, as [start, end);
// start and end are 1e30 once the ray has left the zone for good. Where boxes
// overlap, end is where the box the stretch starts in is left, and asking again
// from there carries on through the box overlapping it. A march that asks again
// each time a sample passes end walks the zone's inside with no test per sample.
void VRSL_ZoneStretchFrom(uint first, uint count, float3 rayOrigin, float3 rayDir,
                          float t, out float start, out float end)
{
    start = 1e30;
    end   = 1e30;
    [loop]
    for (uint b = 0u; b < count; b++)
    {
        float enter, leave;
        if (!VRSL_ZoneBoxSpan(first + 3u * b, rayOrigin, rayDir, enter, leave)
            || leave <= t)
            continue;
        float from = max(enter, t);
        if (from < start || (from == start && leave > end))
        {
            start = from;
            end   = leave;
        }
    }
}

// A zone's inside along one view ray, worked out once per pixel and shared by
// every light in that zone: the first four stretches, merged where boxes
// overlap or abut, sorted, with unused slots at 1e30. Past limit there may be
// more than four, and the stretches there come from VRSL_ZoneStretchFrom.
// last is where the ray leaves the zone for the last time.
struct VRSLZoneStretches
{
    float4 start;
    float4 end;
    float  limit;
    float  last;
};

float VRSL_Min4(float4 v) { return min(min(v.x, v.y), min(v.z, v.w)); }
float VRSL_Max4(float4 v) { return max(max(v.x, v.y), max(v.z, v.w)); }

// Slots of v moved along to make room for, or close up behind, a stretch that
// merged `merged` of the stored ones.
float4 VRSL_ZoneShift(float4 v, float merged)
{
    return merged < 0.5 ? float4(1e30, v.xyz)
         : merged < 1.5 ? v
         : merged < 2.5 ? float4(v.yzw, 1e30)
         : merged < 3.5 ? float4(v.zw, 1e30, 1e30)
         :                float4(v.w, 1e30, 1e30, 1e30);
}

void VRSL_ZoneStretchesAdd(inout VRSLZoneStretches z, float a, float b)
{
    // Stretches closer than this join; no sample lands in a gap that thin.
    const float join = 1e-3;
    // Sorted and apart, so the stretches wholly before [a, b] are a prefix and
    // those wholly after it a suffix; what lies between merges with it.
    bool4 before  = z.end < a - join;
    bool4 after   = z.start > b + join;
    float nBefore = dot((float4)before, 1.0);
    float merged  = 4.0 - nBefore - dot((float4)after, 1.0);
    bool4 inside  = !(before || after);
    float mergedA = min(a, VRSL_Min4(inside ? z.start : 1e30));
    float mergedB = max(b, VRSL_Max4(inside ? z.end : -1e30));

    // A fifth stretch drops whichever is last, and the walk takes over from it.
    if (merged < 0.5 && z.start.w < 1e30)
        z.limit = nBefore > 3.5 ? a : z.start.w;

    bool4 at = float4(0.0, 1.0, 2.0, 3.0) == nBefore;
    z.start = before ? z.start : (at ? mergedA : VRSL_ZoneShift(z.start, merged));
    z.end   = before ? z.end   : (at ? mergedB : VRSL_ZoneShift(z.end, merged));
}

VRSLZoneStretches VRSL_ZoneStretches(uint first, uint count, float3 rayOrigin, float3 rayDir,
                                     float tMax)
{
    VRSLZoneStretches z;
    z.start = 1e30;
    z.end   = 1e30;
    z.limit = 1e30;
    z.last  = -1e30;
    [loop]
    for (uint b = 0u; b < count; b++)
    {
        float enter, leave;
        if (!VRSL_ZoneBoxSpan(first + 3u * b, rayOrigin, rayDir, enter, leave))
            continue;
        enter = max(enter, 0.0);
        leave = min(leave, tMax);
        if (leave <= enter)
            continue;
        z.last = max(z.last, leave);
        leave  = min(leave, z.limit);
        if (leave > enter)
            VRSL_ZoneStretchesAdd(z, enter, leave);
    }
    return z;
}

// The stretch of the ray inside the zone at or after t, as VRSL_ZoneStretchFrom
// gives it, read from the stored stretches where it can be.
void VRSL_ZoneStretchAt(VRSLZoneStretches z, uint first, uint count,
                        float3 rayOrigin, float3 rayDir, float t,
                        out float start, out float end)
{
    bool4 ahead = z.end > t;
    start = VRSL_Min4(ahead ? z.start : 1e30);
    end   = VRSL_Min4(ahead ? z.end : 1e30);
    [branch]
    if (start > z.limit)
        VRSL_ZoneStretchFrom(first, count, rayOrigin, rayDir, max(t, z.limit), start, end);
}

// ──────────────────────────────────────────────────────────────────────────────
// Light evaluation (fragment shader use)
// ──────────────────────────────────────────────────────────────────────────────

// Distance attenuation — matches URP's smoothed inverse-square falloff
float VRSL_DistanceAttenuation(float distSq, float range)
{
    float rangeRcp = 1.0 / max(range, 0.0001);
    float d2 = distSq * rangeRcp * rangeRcp;
    float f = saturate(1.0 - d2 * d2);
    return (f * f) / max(distSq, 0.0001);
}

// Spot cone attenuation — matches URP's GetAngleAttenuation, with an optional
// virtual-apex offset for area-emitter fixtures. emitterDepth pushes the cone's
// conceptual apex back along lightDir by that distance, so at the light's actual
// position the cone has finite radius = emitterDepth × tan(halfAngle) instead of
// converging to a point. emitterDepth = 0 reproduces the point-source behaviour.
//
// A second clamp ensures only surfaces in front of the lens receive contribution.
// Without that, emitterDepth > 0 lets the cone illuminate geometry between the
// virtual apex and the actual lens position — including the inside of the
// fixture body itself, which then bleeds visibly through the outer mesh because
// this pipeline doesn't cast shadows. The 5cm soft transition avoids aliasing
// at the lens plane.
float VRSL_SpotAttenuation(float3 lightDir, float3 toLight, float cosInner,
                           float cosOuter, float emitterDepth)
{
    float3 toApex = toLight - lightDir * emitterDepth;
    float cosAngle = dot(-lightDir, normalize(toApex));
    float t = saturate((cosAngle - cosOuter) * (1.0 / max(cosInner - cosOuter, 0.0001)));

    float forwardOfLens = dot(-toLight, lightDir);
    float lensClip      = smoothstep(0.0, 0.05, forwardOfLens);

    return t * t * lensClip;
}

// Surface (BRDF) evaluation lives in VRSLSurfaceBRDF.hlsl, which pulls in URP's
// BRDF library. It is kept out of this header so the compute kernels — which
// include this file for the struct layouts alone — don't have to compile it.

// ──────────────────────────────────────────────────────────────────────────────
// Volumetric (in-scattering) evaluation
// ──────────────────────────────────────────────────────────────────────────────

// Henyey–Greenstein phase function. g controls anisotropy:
//   g = 0    isotropic
//   g > 0    forward-scatter (bright when looking down the beam)
//   g < 0    back-scatter
float VRSL_HenyeyGreenstein(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = max(1.0 + g2 - 2.0 * g * cosTheta, 0.0001);
    return (1.0 - g2) / (4.0 * VRSL_PI * denom * sqrt(denom));
}

// The largest value the phase function takes at this g, over every angle.
// Closed form: the peak sits at cos(theta) = sign(g), where the denominator
// collapses to (1 - |g|)^2. What the visibility bound multiplies by, so it is
// an upper bound on the phase term wherever the sample lands.
float VRSL_MaxPhase(float g)
{
    float a = abs(g);
    float d = max(1.0 - a, 0.05);
    return (1.0 + a) / (4.0 * VRSL_PI * d * d);
}

// Narrow an existing ray span to the part that lies inside a spot cone.
// tNear/tFar arrive as the span against the light's bounding sphere and are
// tightened in place; returns false when the ray misses the cone entirely.
//
// The sphere is a very loose proxy for a cone. At 20 m range its chord runs to
// 40 m, while a ray crossing a beam near the lens covers well under a metre of
// lit space, so the step budget is spent almost entirely in the dark and the
// handful of samples that do land inside carry the whole result. That is large
// per-pixel quadrature error, and no dither hides error that big — it only
// reshapes it into whatever pattern the dither itself carries.
//
// A single cone nappe is convex, so a ray meets it in exactly one interval. The
// quadratic supplies the surface crossings and midpoint tests pick the inside
// sub-interval, which avoids case analysis on root signs and folds the
// ray-parallel-to-surface degeneracy into the same path.
bool VRSL_NarrowSpanToCone(float3 rayOrigin, float3 rayDir, float3 apex,
                           float3 axis, float cosOuter,
                           inout float tNear, inout float tFar)
{
    float3 co = rayOrigin - apex;
    float  c2 = cosOuter * cosOuter;

    float dv = dot(rayDir, axis);
    float dc = dot(co, axis);

    float a = dv * dv - c2;
    float b = 2.0 * (dv * dc - dot(rayDir, co) * c2);
    float c = dc * dc - dot(co, co) * c2;

    float r0 = tNear;
    float r1 = tNear;

    if (abs(a) > 1e-7)
    {
        float disc = b * b - 4.0 * a * c;
        if (disc > 0.0)
        {
            float sq  = sqrt(disc);
            float inv = 0.5 / a;
            r0 = (-b - sq) * inv;
            r1 = (-b + sq) * inv;
            if (r0 > r1) { float s = r0; r0 = r1; r1 = s; }
        }
    }
    else if (abs(b) > 1e-7)
    {
        r0 = -c / b;
        r1 = r0;
    }

    r0 = clamp(r0, tNear, tFar);
    r1 = clamp(r1, tNear, tFar);

    float bounds[4] = { tNear, r0, r1, tFar };

    float lo =  1e30;
    float hi = -1e30;

    [unroll]
    for (int k = 0; k < 3; k++)
    {
        float s = bounds[k];
        float e = bounds[k + 1];
        if (e - s < 1e-5) continue;

        float3 v     = rayOrigin + rayDir * (0.5 * (s + e)) - apex;
        float  axial = dot(v, axis);
        if (axial <= 0.0) continue;
        if (axial < cosOuter * length(v)) continue;

        lo = min(lo, s);
        hi = max(hi, e);
    }

    if (hi - lo < 1e-5) return false;

    tNear = lo;
    tFar  = hi;
    return true;
}

// Evaluate a single VRSL light's contribution at a point inside the volume.
// viewToCamera is the unit vector pointing from samplePos back toward the camera.
// Returns radiance per unit density per unit length — caller multiplies by
// (density * stepSize) to integrate along the view ray.
float3 VRSL_EvaluateLightVolumetric(VRSLLightData light, float3 samplePos,
                                    float3 viewToCamera, float anisotropy)
{
    if (!VRSL_IsActive(light)) return 0;

    float3 toLight = light.positionAndRange.xyz - samplePos;
    float  distSq  = dot(toLight, toLight);
    float  range   = light.positionAndRange.w;
    if (distSq > range * range) return 0;

    float distAtten = VRSL_DistanceAttenuation(distSq, range);

    float spotAtten = 1.0;
    if (VRSL_LightType(light) < 0.5)
        spotAtten = VRSL_SpotAttenuation(
            light.directionAndType.xyz, toLight,
            light.spotParams.x, light.spotParams.y,
            light.spotParams.z);
    if (spotAtten < 0.0001) return 0;

    // Phase: angle between the view ray (toward camera) and the direction
    // from the sample to the light source.
    float3 toLightN = toLight * rsqrt(max(distSq, 0.0001));
    float  cosTheta = dot(viewToCamera, toLightN);
    float  phase    = VRSL_HenyeyGreenstein(cosTheta, anisotropy);

    return light.colorAndIntensity.xyz * light.colorAndIntensity.w
           * distAtten * spotAtten * phase;
}

// ──────────────────────────────────────────────────────────────────────────────
// Gobo projection — shared by surface lighting and volumetric scattering
// ──────────────────────────────────────────────────────────────────────────────

// Gobo texture array — one slice per unique gobo texture. Slice index lives in
// VRSL_GoboIndex(light) (-1 means no gobo).
Texture2DArray _VRSLGobos;
SamplerState   sampler_linear_clamp;

// Project a world-space point onto the light's gobo texture and return the
// resulting [0,1] grayscale mask (1.0 when no gobo is assigned). spinAngle is
// the fully-integrated rotation in radians, wrapped to [-2π, 2π] by the
// compute shader (see VRSLDMXLightUpdate.compute) — this makes the gobo
// position stay continuous across DMX rate changes.
float SampleGobo(float goboIdx, float spinAngle, float3 posWS,
                 float3 lightPos, float3 lightDir, float cosOuter,
                 float emitterDepth)
{
    if (goboIdx < -0.5) return 1.0;

    // Project from the virtual apex (lightPos - lightDir * emitterDepth) so the
    // UV mapping matches the cone widening that VRSL_SpotAttenuation already
    // applies. Without this offset, points inside the emitterDepth-widened cone
    // sample beyond UV [0,1] and clamp to the gobo texture edge, which on the
    // default circular gobo is black — silently masking emitterDepth's effect.
    float3 toPixel = posWS - lightPos;
    float  depth   = dot(toPixel, lightDir) + emitterDepth;
    if (depth <= 0.0) return 0.0;

    // Switch up-reference near vertical to avoid degenerate cross product.
    float3 worldUp = abs(lightDir.y) < 0.99 ? float3(0, 1, 0) : float3(0, 0, 1);
    float3 right   = normalize(cross(worldUp, lightDir));
    float3 up      = cross(lightDir, right);

    // tan(outerHalfAngle) from the stored cosine — avoids acos / radians.
    float sinOuter = sqrt(max(0.0, 1.0 - cosOuter * cosOuter));
    float tanHalf  = sinOuter / max(cosOuter, 0.0001);

    float uvScale = 0.5 / (depth * tanHalf);
    float u = dot(toPixel, right) * uvScale + 0.5;
    float v = dot(toPixel, up)    * uvScale + 0.5;

    if (spinAngle != 0.0)
    {
        float s = sin(spinAngle), c = cos(spinAngle);
        float cu = u - 0.5, cv = v - 0.5;
        u = c * cu - s * cv + 0.5;
        v = s * cu + c * cv + 0.5;
    }

    return _VRSLGobos.SampleLevel(sampler_linear_clamp,
                                  float3(u, v, goboIdx), 0).r;
}

// ──────────────────────────────────────────────────────────────────────────────
// Discoball — a point light masked by a cubemap of its dots
// ──────────────────────────────────────────────────────────────────────────────

// One cubemap for every discoball in the scene, bound with the gobo wheel by
// VRSLSceneResources. _VRSLDiscoballCubeBound is 0 when no manager has one, and
// the ball then lights as a plain point light.
TextureCube _VRSLDiscoballCube;
float       _VRSLDiscoballCubeBound;

// The dot pattern reaching a world-space point: the cubemap looked up along the
// direction from the ball, turned back by the ball's spin about its axis so the
// pattern turns with the ball. Coloured cubemaps tint their dots. Returns 1 for
// any light that is not a discoball, so it can sit in the loop beside SampleGobo.
float3 VRSL_DiscoballMask(VRSLLightData light, float3 posWS)
{
    if (!VRSL_IsDiscoball(light) || _VRSLDiscoballCubeBound < 0.5) return 1.0;

    float3 d    = normalize(posWS - light.positionAndRange.xyz);
    float3 axis = light.directionAndType.xyz;
    float  a    = -light.spotParams.w;
    float  c    = cos(a), s = sin(a);
    d = d * c + cross(axis, d) * s + axis * dot(axis, d) * (1.0 - c);

    return _VRSLDiscoballCube.SampleLevel(sampler_linear_clamp, d, 0).rgb;
}

// ──────────────────────────────────────────────────────────────────────────────
// URP AudioLink light path — per-fixture config written by VRSL_AudioLinkURPLightManager
// Must exactly match VRSLALFixtureConfig in VRSL_AudioLinkURPLightManager.cs
// ──────────────────────────────────────────────────────────────────────────────
struct VRSLALFixtureConfig
{
    float4 positionAndRange;  // xyz = world pos (updated per-frame), w = range
    float4 forwardAndType;    // xyz = world forward (from tiltTransform or light, per-frame;
                              //       a discoball's up axis), w = light type (0=spot, 1=point, 2=discoball)
    float4 intensityParams;   // x = maxIntensity, y = finalIntensity × globalIntensity,
                              // z = AudioLink active (1=sample AL, 0=static full intensity),
                              // w = discoball beams (1 = the raymarch draws its dots)
    float4 spotAngles;        // x = inner-to-outer ratio (0..1), y = outer half-angle (deg),
                              // z = emitterDepth (m), w = zone slot + 1 (0 = none)
    float4 alParams;          // x = band (0–3), y = delay (0–127), z = bandMultiplier,
                              // w = colorMode (0=emission, 1–4=theme0–3, 5=colorChord,
                              //                6=colorTexture (HSV-normalised),
                              //                7=colorTextureTraditional (raw))
    float4 emissionColor;     // xyz = linear RGB (used when colorMode == 0), w = unused
    float4 reserved;          // x = gobo slot index (-1 = no gobo, 0+ = slice in _VRSLGobos),
                              // y = gobo spin speed (bipolar: 0 = no spin, negative = CCW, positive = CW),
                              //     or a discoball's spin (deg/s),
                              // zw = textureSamplingCoordinates UV (used when colorMode == 6 or 7)
};

// ──────────────────────────────────────────────────────────────────────────────
// Density noise — the field the volumetric raymarch modulates density with
// ──────────────────────────────────────────────────────────────────────────────

// The raymarch does not evaluate this per sample. It samples a baked texture,
// generated once per manager by the BakeVolumetricNoise kernel
// (VRSLVolumetricNoiseBake.hlsl) from VRSL_ValueNoise3DPeriodic below, so the
// field and the function that defines it live in the same file and the texture
// cannot drift from the code that expects it. The procedural form is kept as
// that definition and as the reference a still of the texture is judged against.
//
// The texture is VRSL_VOL_NOISE_SIZE texels per axis over a lattice of
// VRSL_VOL_NOISE_PERIOD cells, so four texels per cell: enough for the sampler's
// linear filter to follow the smoothstep between lattice points rather than
// flatten it to a straight line. In lattice units the field repeats every
// VRSL_VOL_NOISE_PERIOD, which at the shipped noise scale of 0.3 is every 53 m
// of world. A density modulation has no feature a viewer can recognise at a
// second sighting, so the repeat is not readable; it is not an oversight.
// Both constants are mirrored in VRSLVolumetricNoise.cs.
#define VRSL_VOL_NOISE_SIZE        64
#define VRSL_VOL_NOISE_PERIOD      16.0
#define VRSL_VOL_NOISE_INV_PERIOD  (1.0 / 16.0)

// Dave Hoskins-style 3D hash. ~6 ALU per call.
float VRSL_Hash3D(float3 p)
{
    p = frac(p * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

// Smoothed 3D value noise on a unit grid. 8 hash taps + trilinear smoothstep
// interpolation — ~50 ALU per sample. Output range [0,1].
float VRSL_ValueNoise3D(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float n000 = VRSL_Hash3D(i);
    float n100 = VRSL_Hash3D(i + float3(1, 0, 0));
    float n010 = VRSL_Hash3D(i + float3(0, 1, 0));
    float n110 = VRSL_Hash3D(i + float3(1, 1, 0));
    float n001 = VRSL_Hash3D(i + float3(0, 0, 1));
    float n101 = VRSL_Hash3D(i + float3(1, 0, 1));
    float n011 = VRSL_Hash3D(i + float3(0, 1, 1));
    float n111 = VRSL_Hash3D(i + float3(1, 1, 1));

    float n00 = lerp(n000, n100, f.x);
    float n10 = lerp(n010, n110, f.x);
    float n01 = lerp(n001, n101, f.x);
    float n11 = lerp(n011, n111, f.x);
    float n0  = lerp(n00,  n10,  f.y);
    float n1  = lerp(n01,  n11,  f.y);
    return lerp(n0, n1, f.z);
}

// The same field on a lattice that wraps every `period` cells, so a texture
// holding one period tiles with no seam. Inside one period it is
// VRSL_ValueNoise3D exactly, except in the last cell of each axis, where the
// far corner reads lattice point 0 instead of `period`. Expects p >= 0, which
// the bake guarantees; fmod on a negative would wrap the wrong way.
float VRSL_ValueNoise3DPeriodic(float3 p, float period)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float3 i1 = fmod(i + 1.0, period);

    float n000 = VRSL_Hash3D(float3(i.x,  i.y,  i.z));
    float n100 = VRSL_Hash3D(float3(i1.x, i.y,  i.z));
    float n010 = VRSL_Hash3D(float3(i.x,  i1.y, i.z));
    float n110 = VRSL_Hash3D(float3(i1.x, i1.y, i.z));
    float n001 = VRSL_Hash3D(float3(i.x,  i.y,  i1.z));
    float n101 = VRSL_Hash3D(float3(i1.x, i.y,  i1.z));
    float n011 = VRSL_Hash3D(float3(i.x,  i1.y, i1.z));
    float n111 = VRSL_Hash3D(float3(i1.x, i1.y, i1.z));

    float n00 = lerp(n000, n100, f.x);
    float n10 = lerp(n010, n110, f.x);
    float n01 = lerp(n001, n101, f.x);
    float n11 = lerp(n011, n111, f.x);
    float n0  = lerp(n00,  n10,  f.y);
    float n1  = lerp(n01,  n11,  f.y);
    return lerp(n0, n1, f.z);
}

#endif // VRSL_LIGHTING_LIBRARY_INCLUDED
