using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VRSL.URP
{
    /// <summary>
    /// Captures the per-pixel surface data the VRSL lighting pass needs to
    /// evaluate a real BRDF, into VRSL-owned render targets:
    ///
    /// <list type="bullet">
    /// <item><c>_VRSLNormalsTexture</c> — authored world normals, drawn with the
    /// same <c>DepthNormals</c> / <c>DepthNormalsOnly</c> shader tags URP's own
    /// depth-normals prepass uses. Any opaque shader that already supports URP
    /// contributes through its existing pass.</item>
    /// <item><c>_VRSLAlbedoTexture</c> (rgb = base colour, a = smoothness) and
    /// <c>_VRSLMaterialTexture</c> (r = metallic) — drawn with
    /// <c>VRSLSurfaceProperties</c> as a <c>DrawingSettings.overrideShader</c>,
    /// so each renderer keeps its own material's property values. That is what
    /// reaches albedo on shaders VRSL knows nothing about without asking their
    /// authors to add anything.</item>
    /// </list>
    ///
    /// Both targets are non-MSAA regardless of the URP asset's setting, and are
    /// allocated as <c>Tex2DArray</c> with <c>volumeDepth</c> matching the camera
    /// target so per-eye data is correct under single-pass instanced VR.
    ///
    /// Costs two opaque geometry passes. The normals half can't be merged into
    /// the override-shader half without giving up authored normal maps, since a
    /// shader-tag draw renders each material's own pass and an override draw
    /// replaces it.
    ///
    /// Both <see cref="VRSL_URPLightManager"/> (DMX) and
    /// <see cref="VRSL_AudioLinkURPLightManager"/> (AudioLink) instantiate and
    /// enqueue this pass per camera before their lighting passes. When both
    /// managers are active the pass enqueues twice per camera; the second run
    /// overwrites the first with identical data.
    /// </summary>
    public class VRSLSurfacePrepass : ScriptableRenderPass
    {
        // Match URP's standard depth-normals-prepass tag set.
        static readonly List<ShaderTagId> s_NormalsTagIds = new()
        {
            new ShaderTagId("DepthNormals"),
            new ShaderTagId("DepthNormalsOnly"),
        };

        // Opaque forward tags. With an override shader these select *which*
        // renderers draw; the shader itself is replaced, so a material only has
        // to declare one of these LightModes to contribute its albedo.
        static readonly List<ShaderTagId> s_SurfaceTagIds = new()
        {
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("LightweightForward"),
        };

        // The alpha-test queue is split out so an opaque material whose base map
        // stores non-colour data in alpha is never clipped against a stale
        // _Cutoff. 2450 is Unity's AlphaTest queue.
        static readonly RenderQueueRange s_OpaqueRange    = new(2000, 2449);
        static readonly RenderQueueRange s_AlphaTestRange = new(2450, 2500);

        static readonly int s_NormalsTextureID  = Shader.PropertyToID("_VRSLNormalsTexture");
        static readonly int s_AlbedoTextureID   = Shader.PropertyToID("_VRSLAlbedoTexture");
        static readonly int s_MaterialTextureID = Shader.PropertyToID("_VRSLMaterialTexture");

        readonly Shader _surfacePropertiesShader;

        class NormalsPassData
        {
            public RendererListHandle rendererList;
        }

        class SurfacePassData
        {
            public RendererListHandle opaqueList;
            public RendererListHandle alphaTestList;
        }

        /// <param name="surfacePropertiesShader">
        /// <c>Hidden/VRSL-URP/SurfaceProperties</c>. When null the albedo and
        /// material targets are skipped and the lighting pass falls back to a
        /// neutral surface, so the prepass degrades to normals only.
        /// </param>
        public VRSLSurfacePrepass(Shader surfacePropertiesShader)
        {
            _surfacePropertiesShader = surfacePropertiesShader;

            // Before opaque rendering, so the lighting pass at
            // AfterRenderingOpaques sees every target populated.
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
            var dimension = slices > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D;

            var sortFlags = camData.defaultOpaqueSortFlags;

            RecordNormals(rg, renderingData, camData, lightData, sortFlags,
                          width, height, slices, dimension);

            if (_surfacePropertiesShader != null)
                RecordSurfaceProperties(rg, renderingData, camData, lightData, sortFlags,
                                        width, height, slices, dimension);
        }

        void RecordNormals(RenderGraph rg, UniversalRenderingData renderingData,
                           UniversalCameraData camData, UniversalLightData lightData,
                           SortingCriteria sortFlags,
                           int width, int height, int slices, TextureDimension dimension)
        {
            // MSAASamples.None pinned regardless of the URP asset's MSAA setting —
            // that's the payoff of running our own prepass rather than reading
            // URP's _CameraNormalsTexture handle.
            var normalsDesc = new TextureDesc(width, height)
            {
                name        = "_VRSLNormalsTexture",
                format      = GraphicsFormat.R8G8B8A8_SNorm,
                clearBuffer = true,
                clearColor  = Color.clear,
                dimension   = dimension,
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
                dimension       = dimension,
                slices          = slices,
                msaaSamples     = MSAASamples.None,
            };

            TextureHandle normalsRT = rg.CreateTexture(normalsDesc);
            TextureHandle depthRT   = rg.CreateTexture(depthDesc);

            var drawSettings = RenderingUtils.CreateDrawingSettings(
                s_NormalsTagIds, renderingData, camData, lightData, sortFlags);

            using var builder = rg.AddRasterRenderPass<NormalsPassData>(
                "VRSL Normals Prepass", out var data);

            data.rendererList = rg.CreateRendererList(new RendererListParams(
                renderingData.cullResults, drawSettings,
                new FilteringSettings(RenderQueueRange.opaque)));

            builder.UseRendererList(data.rendererList);
            builder.SetRenderAttachment(normalsRT, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depthRT, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(normalsRT, s_NormalsTextureID);

            builder.SetRenderFunc((NormalsPassData p, RasterGraphContext ctx) =>
            {
                ctx.cmd.DrawRendererList(p.rendererList);
            });
        }

        void RecordSurfaceProperties(RenderGraph rg, UniversalRenderingData renderingData,
                                     UniversalCameraData camData, UniversalLightData lightData,
                                     SortingCriteria sortFlags,
                                     int width, int height, int slices, TextureDimension dimension)
        {
            // sRGB so the 8-bit albedo store keeps its precision in the darks —
            // the sample in the lighting shader decodes back to linear. Alpha
            // (smoothness) is unaffected by the sRGB transfer.
            var albedoDesc = new TextureDesc(width, height)
            {
                name        = "_VRSLAlbedoTexture",
                format      = GraphicsFormat.R8G8B8A8_SRGB,
                clearBuffer = true,
                clearColor  = Color.clear,
                dimension   = dimension,
                slices      = slices,
                msaaSamples = MSAASamples.None,
                filterMode  = FilterMode.Point,
                wrapMode    = TextureWrapMode.Clamp,
            };
            var materialDesc = new TextureDesc(width, height)
            {
                name        = "_VRSLMaterialTexture",
                format      = GraphicsFormat.R8_UNorm,
                clearBuffer = true,
                clearColor  = Color.clear,
                dimension   = dimension,
                slices      = slices,
                msaaSamples = MSAASamples.None,
                filterMode  = FilterMode.Point,
                wrapMode    = TextureWrapMode.Clamp,
            };
            var depthDesc = new TextureDesc(width, height)
            {
                name            = "VRSL Surface Depth",
                depthBufferBits = DepthBits.Depth32,
                clearBuffer     = true,
                dimension       = dimension,
                slices          = slices,
                msaaSamples     = MSAASamples.None,
            };

            TextureHandle albedoRT   = rg.CreateTexture(albedoDesc);
            TextureHandle materialRT = rg.CreateTexture(materialDesc);
            TextureHandle depthRT    = rg.CreateTexture(depthDesc);

            var opaqueSettings = RenderingUtils.CreateDrawingSettings(
                s_SurfaceTagIds, renderingData, camData, lightData, sortFlags);
            opaqueSettings.overrideShader          = _surfacePropertiesShader;
            opaqueSettings.overrideShaderPassIndex = 0;

            var alphaTestSettings = opaqueSettings;
            alphaTestSettings.overrideShaderPassIndex = 1;

            using var builder = rg.AddRasterRenderPass<SurfacePassData>(
                "VRSL Surface Properties Prepass", out var data);

            data.opaqueList = rg.CreateRendererList(new RendererListParams(
                renderingData.cullResults, opaqueSettings,
                new FilteringSettings(s_OpaqueRange)));
            data.alphaTestList = rg.CreateRendererList(new RendererListParams(
                renderingData.cullResults, alphaTestSettings,
                new FilteringSettings(s_AlphaTestRange)));

            builder.UseRendererList(data.opaqueList);
            builder.UseRendererList(data.alphaTestList);
            builder.SetRenderAttachment(albedoRT,   0, AccessFlags.Write);
            builder.SetRenderAttachment(materialRT, 1, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depthRT, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(albedoRT,   s_AlbedoTextureID);
            builder.SetGlobalTextureAfterPass(materialRT, s_MaterialTextureID);

            builder.SetRenderFunc((SurfacePassData p, RasterGraphContext ctx) =>
            {
                ctx.cmd.DrawRendererList(p.opaqueList);
                ctx.cmd.DrawRendererList(p.alphaTestList);
            });
        }
    }
}
