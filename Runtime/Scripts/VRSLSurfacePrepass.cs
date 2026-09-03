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
    /// Both draws honour <see cref="Layers"/>. Geometry on a layer outside it is
    /// not drawn into either target, and the lighting pass shades it as the
    /// neutral fallback surface with a depth-derived normal.
    ///
    /// Both <see cref="VRSL_URPLightManager"/> (DMX) and
    /// <see cref="VRSL_AudioLinkURPLightManager"/> (AudioLink) instantiate this
    /// pass, but only one enqueues it per camera: the output is identical either
    /// way, so the DMX manager owns it and the AudioLink manager defers while a
    /// DMX manager is present, enabled and drawing fixtures through that camera.
    ///
    /// Holds no GPU resources and so is deliberately not <c>IDisposable</c>,
    /// unlike <see cref="VRSLTileCullPass"/>. Every target here comes from
    /// <c>RenderGraph.CreateTexture</c>, which the graph pools and frees itself.
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
        static readonly int s_SurfaceDepthID    = Shader.PropertyToID("_VRSLSurfaceDepthTexture");

        // 1 when the camera depth texture is available to the override draw, 0
        // otherwise. Without it the depth gate in VRSLSurfaceProperties would
        // compare against an unbound texture and clip every fragment, which is a
        // far worse failure than the mismatched albedo the gate exists to stop.
        static readonly int s_DepthGateID       = Shader.PropertyToID("_VRSLSurfaceDepthGate");

        readonly Shader _surfacePropertiesShader;

        /// <summary>Layers both draws include. Set by the owning manager before
        /// each enqueue, so an author's change applies on the next frame.</summary>
        public LayerMask Layers { get; set; } = ~0;

        class NormalsPassData
        {
            public RendererListHandle rendererList;
        }

        class SurfacePassData
        {
            public RendererListHandle opaqueList;
            public RendererListHandle alphaTestList;
            public bool depthGate;
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

            // The override-shader draw tests each fragment against the camera's
            // depth (see VRSLSurfaceProperties), so _CameraDepthTexture has to be
            // populated by the time this pass runs. Requesting it from a pass
            // scheduled before opaques is what forces URP to satisfy it with a
            // depth prepass rather than a copy after opaques.
            ConfigureInput(ScriptableRenderPassInput.Depth);
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
                RecordSurfaceProperties(rg, frame.Get<UniversalResourceData>(),
                                        renderingData, camData, lightData, sortFlags,
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
                new FilteringSettings(RenderQueueRange.opaque, Layers)));

            builder.UseRendererList(data.rendererList);
            builder.SetRenderAttachment(normalsRT, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depthRT, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(normalsRT, s_NormalsTextureID);

            builder.SetRenderFunc((NormalsPassData p, RasterGraphContext ctx) =>
            {
                ctx.cmd.DrawRendererList(p.rendererList);
            });
        }

        void RecordSurfaceProperties(RenderGraph rg, UniversalResourceData resources,
                                     UniversalRenderingData renderingData,
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
            // Published to the lighting pass, which rejects albedo wherever this
            // disagrees with the camera's depth. An override shader replaces the
            // material's own shader outright, so any visibility logic living in
            // that shader — Poiyomi's UDIM discard, alpha clips, vertex
            // displacement — never runs here and this pass draws geometry the
            // camera didn't keep. Comparing the two depths is what catches that.
            var depthDesc = new TextureDesc(width, height)
            {
                name            = "_VRSLSurfaceDepthTexture",
                depthBufferBits = DepthBits.Depth32,
                clearBuffer     = true,
                dimension       = dimension,
                slices          = slices,
                msaaSamples     = MSAASamples.None,
                filterMode      = FilterMode.Point,
                wrapMode        = TextureWrapMode.Clamp,
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
                new FilteringSettings(s_OpaqueRange, Layers)));
            data.alphaTestList = rg.CreateRendererList(new RendererListParams(
                renderingData.cullResults, alphaTestSettings,
                new FilteringSettings(s_AlphaTestRange, Layers)));

            builder.UseRendererList(data.opaqueList);
            builder.UseRendererList(data.alphaTestList);
            builder.SetRenderAttachment(albedoRT,   0, AccessFlags.Write);
            builder.SetRenderAttachment(materialRT, 1, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depthRT, AccessFlags.Write);
            builder.SetGlobalTextureAfterPass(albedoRT,   s_AlbedoTextureID);
            builder.SetGlobalTextureAfterPass(materialRT, s_MaterialTextureID);
            builder.SetGlobalTextureAfterPass(depthRT,    s_SurfaceDepthID);

            // The override draw samples the camera depth to reject geometry the
            // camera dropped. ConfigureInput asks URP to produce that texture;
            // declaring the read here is what makes Render Graph order this pass
            // after whatever produces it, and is why the other passes that sample
            // it do the same. Requesting it is not the same as declaring it.
            data.depthGate = resources.cameraDepthTexture.IsValid();
            if (data.depthGate)
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);

            // Setting a global from a raster pass requires this, or Unity throws
            // on the SetGlobalFloat below.
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((SurfacePassData p, RasterGraphContext ctx) =>
            {
                // Depth unavailable degrades to drawing everything, matching the
                // behaviour before the gate existed. Clipping against an unbound
                // texture would instead reject every fragment and leave the whole
                // scene on the neutral fallback.
                ctx.cmd.SetGlobalFloat(s_DepthGateID, p.depthGate ? 1f : 0f);
                ctx.cmd.DrawRendererList(p.opaqueList);
                ctx.cmd.DrawRendererList(p.alphaTestList);
            });
        }
    }
}
