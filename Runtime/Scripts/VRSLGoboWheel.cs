using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace VRSL.URP
{
    /// <summary>
    /// Packs the scene's gobo wheel into the <c>Texture2DArray</c> the lighting
    /// and volumetric shaders sample as <c>_VRSLGobos</c>.
    ///
    /// Built entirely on the GPU. The slices are produced by blitting each source
    /// straight into a layer of an array render texture, so there is no readback
    /// and no per-slice CPU upload — rebuilding the wheel doesn't stall the frame.
    ///
    /// Slice resolution follows the largest source rather than a fixed size, so a
    /// detailed gobo isn't quietly resampled down.
    ///
    /// The shaders read a slice's RGB and multiply it into the light. A grey gobo is
    /// stored with its red channel in all three, the mask the light is cut by; a gobo
    /// in colour keeps its RGB, premultiplied by its alpha. The array is linear unless
    /// it holds a gobo in colour, then sRGB, which keeps dark colours that linear 8-bit
    /// storage would band.
    /// </summary>
    public static class VRSLGoboWheel
    {
        static readonly int s_Pack = Shader.PropertyToID("_VRSLGoboPack");

        /// <summary>Loaded from Resources, so it ships in every build with the package.</summary>
        internal const string PackShaderResource = "VRSL-URP-GoboWheelPack";

        const float PackRed = 0f, PackAlpha = 1f, PackColor = 2f;

        static Material s_pack;
        static bool     s_packMissing;

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
            => Build(sources, null, out sliceCount, out complete);

        /// <summary>
        /// Build the array with the gobos listed in <paramref name="colorSources"/> in
        /// colour and the rest grey. See the other overload.
        /// </summary>
        public static RenderTexture Build(Texture2D[] sources, Texture2D[] colorSources,
                                          out int sliceCount, out bool complete)
        {
            sliceCount = sources?.Length ?? 0;
            complete   = true;
            if (sliceCount == 0) return null;
            complete = Resident(sources);

            bool anyColor = false;
            for (int i = 0; i < sources.Length; i++)
                if (PacksInColor(sources[i], colorSources)) { anyColor = true; break; }

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
                sRGB             = anyColor,
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

            var pack = PackMaterial();
            var previous = RenderTexture.active;
            for (int i = 0; i < sliceCount; i++)
            {
                // Blitting into a specific destination slice keeps the whole build
                // on the GPU; the readback path this replaces stalled once per slot.
                // An empty slot is written white, an open gobo: Create does not clear
                // the array, and a slice left alone holds whatever the allocation
                // returned, which the shaders would sample as a mask.
                Texture source = sources[i] != null ? sources[i] : Texture2D.whiteTexture;
                if (pack == null)
                {
                    Graphics.Blit(source, array, 0, i);
                    continue;
                }
                pack.SetFloat(s_Pack, PackMode(sources[i], colorSources));
                Graphics.Blit(source, array, pack, 0, i);
            }
            RenderTexture.active = previous;
            if (complete) ReleaseMipRequests(sources);

            return array;
        }

        /// <summary>Whether <paramref name="source"/> is stored in colour: listed, and
        /// with more than one channel to keep.</summary>
        internal static bool PacksInColor(Texture2D source, Texture2D[] colorSources)
            => source != null
            && VRSLGoboWheelAsset.InColor(colorSources, source)
            && GraphicsFormatUtility.GetComponentCount(source.graphicsFormat) > 1;

        static float PackMode(Texture2D source, Texture2D[] colorSources)
        {
            if (PacksInColor(source, colorSources)) return PackColor;
            // An alpha-only texture reads black in its red channel on some APIs, so its
            // mask comes from the alpha it does carry.
            if (source != null && GraphicsFormatUtility.IsAlphaOnlyFormat(source.graphicsFormat)) return PackAlpha;
            return PackRed;
        }

        static Material PackMaterial()
        {
            if (s_pack != null) return s_pack;
            if (s_packMissing) return null;
            var shader = Resources.Load<Shader>(PackShaderResource);
            if (shader == null || !shader.isSupported)
            {
                s_packMissing = true;
                Debug.LogWarning("[VRSL] The gobo wheel's packing shader is missing from this build, so gobos "
                               + "project without their colour and grey gobos may carry a faint tint. "
                               + "Reinstall the VRSL-URP package to put it back.");
                return null;
            }
            s_pack = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return s_pack;
        }

        /// <summary>Let go of the packing material; the next build makes it again.</summary>
        internal static void ReleasePack()
        {
            s_packMissing = false;
            if (s_pack == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying) Object.DestroyImmediate(s_pack);
            else
#endif
            Object.Destroy(s_pack);
            s_pack = null;
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
