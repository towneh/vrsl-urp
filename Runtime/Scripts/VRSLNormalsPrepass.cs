using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VRSL.URP
{
    /// <summary>
    /// Renders opaque scene geometry into a VRSL-owned non-MSAA normals RT,
    /// using the same <c>DepthNormals</c> / <c>DepthNormalsOnly</c> shader tags
    /// URP's built-in depth-normals prepass uses. Opaque shaders that already
    /// support URP (URP Lit, Poiyomi URP, lilToon URP, Mochie URP, etc.)
    /// contribute their authored normals through their existing pass — third-
    /// party shader authors don't need to add anything VRSL-specific.
    ///
    /// The output is bound globally as <c>_VRSLNormalsTexture</c> after the
    /// pass runs; the surface lighting shader samples that texture and falls
    /// back to a depth-derivative reconstruction on pixels where no normal
    /// was written (surfaces drawn by shaders without a URP DepthNormals
    /// pass), so those surfaces still pick up VRSL light, just faceted to
    /// the underlying tessellation.
    ///
    /// Both <see cref="VRSL_URPLightManager"/> (DMX) and
    /// <see cref="VRSL_AudioLinkURPLightManager"/> (AudioLink) instantiate
    /// and enqueue this pass per camera before their respective lighting
    /// passes. When both managers are active simultaneously the pass enqueues
    /// twice per camera; the second run overwrites the first with identical
    /// data — measurable but acceptable cost for the rare dual-manager case.
    /// </summary>
    public class VRSLNormalsPrepass : ScriptableRenderPass
    {
        // Match URP's standard depth-normals-prepass tag set. Opaque shaders
        // that ship a DepthNormals or DepthNormalsOnly pass write to our RT
        // automatically; shaders that don't are silently skipped and fall
        // through to the depth-derivative path in the lighting shader.
        static readonly List<ShaderTagId> s_ShaderTagIds = new()
        {
            new ShaderTagId("DepthNormals"),
            new ShaderTagId("DepthNormalsOnly"),
        };

        static readonly int s_VRSLNormalsTextureID = Shader.PropertyToID("_VRSLNormalsTexture");

        class PassData
        {
            public RendererListHandle rendererList;
        }

        public VRSLNormalsPrepass()
        {
            // Slot before opaque rendering so the lighting pass at
            // AfterRenderingOpaques sees the texture populated.
            renderPassEvent = RenderPassEvent.AfterRenderingPrePasses;
        }

        public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
        {
            var camData       = frame.Get<UniversalCameraData>();
            var renderingData = frame.Get<UniversalRenderingData>();
            var lightData     = frame.Get<UniversalLightData>();

            int width  = camData.cameraTargetDescriptor.width;
            int height = camData.cameraTargetDescriptor.height;
            int slices = Mathf.Max(1, camData.cameraTargetDescriptor.volumeDepth);

            // Per-eye Tex2DArray under SPI VR (volumeDepth == 2). MSAASamples.None
            // pinned regardless of the URP asset's MSAA setting — that's the
            // architectural payoff of running our own prepass instead of reading
            // URP's _CameraNormalsTexture handle.
            var normalsDesc = new TextureDesc(width, height)
            {
                name        = "_VRSLNormalsTexture",
                format      = GraphicsFormat.R8G8B8A8_SNorm,
                clearBuffer = true,
                clearColor  = Color.clear,
                dimension   = slices > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D,
                slices      = slices,
                msaaSamples = MSAASamples.None,
                filterMode  = FilterMode.Point,
                wrapMode    = TextureWrapMode.Clamp,
            };
            var depthDesc = new TextureDesc(width, height)
            {
                name            = "VRSL Normals Depth",
                depthBufferBits = DepthBits.Depth32,
                clearBuffer     = true,
                dimension       = slices > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D,
                slices          = slices,
                msaaSamples     = MSAASamples.None,
            };

            TextureHandle normalsRT = rg.CreateTexture(normalsDesc);
            TextureHandle depthRT   = rg.CreateTexture(depthDesc);

            var sortFlags     = camData.defaultOpaqueSortFlags;
            var drawSettings  = RenderingUtils.CreateDrawingSettings(
                                    s_ShaderTagIds, renderingData, camData, lightData, sortFlags);
            var filterSettings = new FilteringSettings(RenderQueueRange.opaque);

            using var builder = rg.AddRasterRenderPass<PassData>("VRSL Normals Prepass", out var data);

            data.rendererList = rg.CreateRendererList(new RendererListParams(
                renderingData.cullResults, drawSettings, filterSettings));

            builder.UseRendererList(data.rendererList);
            builder.SetRenderAttachment(normalsRT, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depthRT, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(normalsRT, s_VRSLNormalsTextureID);

            builder.SetRenderFunc((PassData p, RasterGraphContext ctx) =>
            {
                ctx.cmd.DrawRendererList(p.rendererList);
            });
        }
    }
}
