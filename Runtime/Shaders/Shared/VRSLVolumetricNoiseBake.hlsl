// The BakeVolumetricNoise kernel. Included by both light-update computes rather
// than living in one of them, so whichever manager is running bakes the texture
// with the compute it already holds and neither carries a copy of the kernel.
//
// One thread per texel. Texel centres are baked, so a sample taken at a lattice
// point reads the field at that point rather than half a texel away from it.
#ifndef VRSL_VOLUMETRIC_NOISE_BAKE_INCLUDED
#define VRSL_VOLUMETRIC_NOISE_BAKE_INCLUDED

RWTexture3D<float> _VRSLVolNoiseOut;

[numthreads(4, 4, 4)]
void BakeVolumetricNoise(uint3 id : SV_DispatchThreadID)
{
    if (any(id >= (uint)VRSL_VOL_NOISE_SIZE)) return;
    float3 p = ((float3)id + 0.5)
             * (VRSL_VOL_NOISE_PERIOD / (float)VRSL_VOL_NOISE_SIZE);
    _VRSLVolNoiseOut[id] = VRSL_ValueNoise3DPeriodic(p, VRSL_VOL_NOISE_PERIOD);
}

#endif // VRSL_VOLUMETRIC_NOISE_BAKE_INCLUDED
