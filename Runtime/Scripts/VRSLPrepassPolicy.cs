using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VRSL.URP
{
    /// <summary>
    /// Where the lighting pass's normals come from for a given camera: URP's own
    /// depth-normals prepass, or a draw VRSL makes itself.
    ///
    /// URP's texture holds the same data VRSL's draw would, from the same shader
    /// passes, wherever URP can produce it at all. It cannot produce it on a
    /// camera that is both multisampled and depth primed: the prepass then draws
    /// into the multisampled depth attachment beside a normals target that is
    /// never multisampled, and the frame fails outright rather than rendering
    /// wrong. Stock URP declines to prime on such a camera, a project's own URP
    /// may not, and this package cannot tell which it is running on, so the
    /// decision refuses whenever priming could be on and the camera is
    /// multisampled. Deferred renderers pack their normals differently and are
    /// refused too, and so is a manager whose Lit surfaces mask leaves layers
    /// out: URP's prepass draws every layer, and a surface left out of VRSL's
    /// is meant to light with a depth-derived normal, not an authored one.
    ///
    /// One place decides, on the CPU, and the lighting shader samples one name
    /// whichever way it went.
    /// </summary>
    public static class VRSLPrepassPolicy
    {
        public readonly struct Decision
        {
            public readonly bool   UseUrpNormals;
            /// <summary>Why, in an author's words. Read by the diagnostics and the
            /// renderer validation.</summary>
            public readonly string Reason;

            public Decision(bool useUrpNormals, string reason)
            {
                UseUrpNormals = useUrpNormals;
                Reason        = reason;
            }
        }

        /// <summary>
        /// The sample count a camera will render at, worked out the way URP does
        /// before it renders. A camera with a target takes the target's count; one
        /// without takes the pipeline asset's. Anything URP goes on to lower it by
        /// makes this an over-estimate, which errs towards drawing VRSL's own
        /// normals and never towards asking for a texture that cannot be drawn.
        /// </summary>
        public static int PredictMsaa(Camera cam, UniversalRenderPipelineAsset asset)
        {
            if (cam == null || asset == null) return 1;
            if (!cam.allowMSAA || asset.msaaSampleCount <= 1) return 1;
            if (cam.targetTexture != null) return Mathf.Max(1, cam.targetTexture.antiAliasing);
            return asset.msaaSampleCount;
        }

        /// <summary>The renderer data a camera's renderer was built from, or null
        /// where it is not a Universal Renderer or cannot be matched.</summary>
        public static UniversalRendererData RendererDataFor(UniversalRenderPipelineAsset asset,
                                                            ScriptableRenderer renderer)
        {
            if (asset == null || renderer == null) return null;
            var list = asset.rendererDataList;
            for (int i = 0; i < list.Length; i++)
                if (ReferenceEquals(asset.GetRenderer(i), renderer))
                    return list[i] as UniversalRendererData;
            return null;
        }

        /// <summary>Decide for a camera about to render.</summary>
        public static Decision Decide(Camera cam, ScriptableRenderer renderer, bool forceOwnNormals,
                                      LayerMask prepassLayers)
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            return Decide(PredictMsaa(cam, asset), RendererDataFor(asset, renderer), forceOwnNormals,
                          prepassLayers);
        }

        /// <summary>The decision itself, a pure function of what it depends on.</summary>
        public static Decision Decide(int msaa, UniversalRendererData renderer, bool forceOwnNormals,
                                      LayerMask prepassLayers)
        {
            if (forceOwnNormals)
                return new Decision(false,
                    "VRSL draws its own normals: Force own normals is on.");

            if (prepassLayers != ~0)
                return new Decision(false,
                    "VRSL draws its own normals: Lit surfaces leaves layers out, and URP's "
                  + "prepass draws every layer.");

            if (renderer == null)
                return new Decision(false,
                    "VRSL draws its own normals: the camera's renderer is not a Universal "
                  + "Renderer, so what its prepass writes is unknown.");

            if (renderer.renderingMode is RenderingMode.Deferred or RenderingMode.DeferredPlus)
                return new Decision(false,
                    $"VRSL draws its own normals: the renderer is {renderer.renderingMode}, "
                  + "which packs its normals differently.");

            if (msaa > 1 && renderer.depthPrimingMode != DepthPrimingMode.Disabled)
                return new Decision(false,
                    $"VRSL draws its own normals: MSAA is {msaa}x and depth priming is "
                  + $"{renderer.depthPrimingMode}. URP cannot draw its normals prepass into a "
                  + "multisampled depth, and whether it would try depends on the URP this "
                  + "project ships. Turn MSAA off, or set priming to Disabled, to skip the "
                  + "extra geometry pass.");

            return new Decision(true,
                msaa > 1
                    ? $"URP's prepass supplies the normals, so VRSL skips its own draw: MSAA is "
                    + $"{msaa}x but depth priming is Disabled."
                    : "URP's prepass supplies the normals, so VRSL skips its own draw.");
        }
    }
}
