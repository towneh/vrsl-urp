using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace VRSL.URP
{
    /// <summary>
    /// The density field the volumetric raymarch samples, baked into a repeating
    /// 3D texture.
    /// </summary>
    /// <remarks>
    /// Generated rather than shipped. The kernel evaluates the lighting library's
    /// own noise function, so the texture cannot drift from the code that expects
    /// it, and its period is an implementation detail rather than an import
    /// setting. Each manager bakes its own on the first frame it needs one and
    /// releases it on disable: one dispatch and 256 KB, cheap enough that sharing
    /// it between managers would save nothing worth a lifetime that has to survive
    /// domain reloads.
    /// </remarks>
    public static class VRSLVolumetricNoise
    {
        /// <summary>Texels per axis. Mirrors <c>VRSL_VOL_NOISE_SIZE</c> in
        /// <c>VRSLLightingLibrary.hlsl</c>.</summary>
        public const int Size = 64;

        /// <summary>Lattice cells per axis, which is the distance in lattice units
        /// at which the field repeats. Mirrors <c>VRSL_VOL_NOISE_PERIOD</c>.</summary>
        public const int Period = 16;

        public const string Kernel = "BakeVolumetricNoise";

        static Texture3D s_fallback;
        static bool      s_warned;

        /// <summary>
        /// Bake the field with <paramref name="compute"/>, which is either
        /// light-update compute. Returns the shared white fallback, and warns once,
        /// when the compute cannot bake it.
        /// </summary>
        public static Texture Bake(ComputeShader compute, Object context)
        {
            if (compute == null || !compute.HasKernel(Kernel))
            {
                Warn("the light-update compute has no " + Kernel + " kernel", context);
                return Fallback();
            }

            var rt = new RenderTexture(Size, Size, 0, GraphicsFormat.R8_UNorm)
            {
                name              = "VRSL Volumetric Noise",
                dimension         = TextureDimension.Tex3D,
                volumeDepth       = Size,
                enableRandomWrite = true,
                wrapMode          = TextureWrapMode.Repeat,
                filterMode        = FilterMode.Bilinear,
                hideFlags         = HideFlags.HideAndDontSave,
            };
            if (!rt.Create())
            {
                Object.DestroyImmediate(rt);
                Warn("the noise texture could not be created", context);
                return Fallback();
            }

            int kernel = compute.FindKernel(Kernel);
            compute.SetTexture(kernel, "_VRSLVolNoiseOut", rt);
            compute.Dispatch(kernel, Size / 4, Size / 4, Size / 4);
            return rt;
        }

        /// <summary>Whether <paramref name="texture"/> is a baked field rather
        /// than the fallback.</summary>
        public static bool IsBaked(Texture texture) => texture is RenderTexture;

        /// <summary>
        /// A single white texel. The field reads 1 everywhere, so density is left
        /// unmodulated: beams lose their haze and keep everything else, which is
        /// the failure worth having over an unbound texture reading 0 and dimming
        /// every beam to a third.
        /// </summary>
        public static Texture Fallback()
        {
            if (s_fallback == null)
            {
                s_fallback = new Texture3D(1, 1, 1, TextureFormat.R8, false)
                {
                    name      = "VRSL Volumetric Noise (fallback)",
                    wrapMode  = TextureWrapMode.Repeat,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                s_fallback.SetPixels32(new[] { new Color32(255, 255, 255, 255) });
                // Left readable: one byte, and a row reads it back to prove it is 1.
                s_fallback.Apply(false, false);
            }
            return s_fallback;
        }

        /// <summary>Release a baked texture. The fallback is shared and is left
        /// alone.</summary>
        public static void Release(ref Texture texture)
        {
            if (texture is RenderTexture rt)
            {
                rt.Release();
                Object.DestroyImmediate(rt);
            }
            texture = null;
        }

        /// <summary>The warning fires once per session. A row that expects it has
        /// to be able to re-arm it.</summary>
        internal static void ResetWarningForTests() => s_warned = false;

        static void Warn(string reason, Object context)
        {
            if (s_warned) return;
            s_warned = true;
            Debug.LogWarning("[VRSL URP] " + reason + ", so beams render with no haze "
                           + "in them. Assign the package's own compute shader on the "
                           + "manager to get it back.", context);
        }
    }
}
