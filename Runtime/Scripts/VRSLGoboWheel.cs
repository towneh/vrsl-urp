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
        /// Whether every source has its full mip chain on the GPU, asking the mip
        /// streamer for it where it does not.
        /// </summary>
        /// <remarks>
        /// The blit below samples whatever mips are resident at that moment. With
        /// mip streaming on, a texture that has not been looked at yet holds only
        /// its smallest mips, and a wheel built from one carries a soft-edged gobo
        /// for the whole session: a sharp disc and a blurred disc differ in a ring
        /// at the edge, which is how it was found. A source that opts out of
        /// streaming is always resident.
        /// </remarks>
        public static bool Resident(Texture2D[] sources)
        {
            if (sources == null) return true;
            bool resident = true;
            foreach (var source in sources)
            {
                if (source == null || !source.streamingMipmaps) continue;
                source.requestedMipmapLevel = 0;
                if (!source.IsRequestedMipmapLevelLoaded()) resident = false;
            }
            return resident;
        }

        /// <summary>Let the streamer drop the mips again once the wheel holds
        /// them.</summary>
        static void ReleaseMipRequests(Texture2D[] sources)
        {
            foreach (var source in sources)
                if (source != null && source.streamingMipmaps)
                    source.ClearRequestedMipmapLevel();
        }

        /// <summary>
        /// Build the array. Returns null when there is nothing to pack. The caller
        /// owns the result and should pass it to <see cref="Release"/>.
        /// <paramref name="complete"/> is false when a source was not fully
        /// resident, in which case the wheel is usable but soft and the caller
        /// should build it again once <see cref="Resident"/> says so.
        /// </summary>
        public static RenderTexture Build(Texture2D[] sources, out int sliceCount, out bool complete)
        {
            sliceCount = sources?.Length ?? 0;
            complete   = true;
            if (sliceCount == 0) return null;
            complete = Resident(sources);

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
            if (complete) ReleaseMipRequests(sources);

            return array;
        }

        public static void Release(ref RenderTexture array)
        {
            if (array == null) return;
            array.Release();
            // Object.Destroy is illegal outside play mode. The managers don't run
            // OnEnable/OnDisable in the editor today (neither carries
            // ExecuteAlways), but this is public and reachable from editor
            // tooling through RefreshFixtures, so it shouldn't depend on that.
#if UNITY_EDITOR
            if (!Application.isPlaying) Object.DestroyImmediate(array);
            else
#endif
            Object.Destroy(array);
            array = null;
        }
    }
}
