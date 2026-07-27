using UnityEngine;
using UnityEngine.Rendering;

namespace VRSL.URP
{
    /// <summary>
    /// Packs the manager's gobo textures into the <c>Texture2DArray</c> the
    /// lighting and volumetric shaders sample as <c>_VRSLGobos</c>.
    ///
    /// Built entirely on the GPU. The slices are produced by blitting each source
    /// straight into a layer of an array render texture, so there is no readback
    /// and no per-slice CPU upload — rebuilding the wheel doesn't stall the frame.
    ///
    /// Slice resolution follows the largest source rather than a fixed size, so a
    /// detailed gobo isn't quietly resampled down. Linear, because a gobo is a
    /// mask rather than colour: the shaders read <c>.r</c>.
    /// </summary>
    public static class VRSLGoboWheel
    {
        /// <summary>Lower bound on slice resolution.</summary>
        public const int MinResolution = 64;

        /// <summary>Upper bound, so a stray 8K source can't allocate an
        /// enormous array. Sources above this are resampled down.</summary>
        public const int MaxResolution = 1024;

        /// <summary>
        /// Build the array. Returns null when there is nothing to pack. The caller
        /// owns the result and should pass it to <see cref="Release"/>.
        /// </summary>
        public static RenderTexture Build(Texture2D[] sources, out int sliceCount)
        {
            sliceCount = sources?.Length ?? 0;
            if (sliceCount == 0) return null;

            int resolution = MinResolution;
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] == null) continue;
                resolution = Mathf.Max(resolution, Mathf.Max(sources[i].width, sources[i].height));
            }
            resolution = Mathf.Clamp(resolution, MinResolution, MaxResolution);

            var desc = new RenderTextureDescriptor(resolution, resolution,
                                                   RenderTextureFormat.ARGB32, 0)
            {
                dimension        = TextureDimension.Tex2DArray,
                volumeDepth      = sliceCount,
                sRGB             = false,
                useMipMap        = false,
                autoGenerateMips = false,
                msaaSamples      = 1,
            };

            var array = new RenderTexture(desc)
            {
                name       = "_VRSLGobos",
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
                hideFlags  = HideFlags.HideAndDontSave,
            };
            array.Create();

            var previous = RenderTexture.active;
            for (int i = 0; i < sliceCount; i++)
            {
                if (sources[i] == null) continue;
                // Blitting into a specific destination slice keeps the whole build
                // on the GPU; the readback path this replaces stalled once per slot.
                Graphics.Blit(sources[i], array, 0, i);
            }
            RenderTexture.active = previous;

            return array;
        }

        public static void Release(ref RenderTexture array)
        {
            if (array == null) return;
            array.Release();
            Object.Destroy(array);
            array = null;
        }
    }
}
