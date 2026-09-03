using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

// RenderingUtils.fullscreenMesh is deprecated in favour of Blitter, and the
// warning is suppressed here rather than acted on. The DrawMesh path was chosen
// deliberately: Blitter's replacement overloads all require a source texture,
// which these passes don't have — they invoke a shader over a fullscreen
// triangle reading globals and additive-blend onto the existing target — and
// Blitter's own implementation falls back to DrawMesh on some platforms anyway.
// Migrating would mean rewriting the vertex stage of every fullscreen pass to
// generate positions from SV_VertexID, and the thing it would change is XR
// correctness, which can only be verified in a headset.
//
// Scoped to this file. If another CS0618 appears here it will be hidden too, so
// re-check on any obsolete-API sweep.
#pragma warning disable 618

namespace VRSL.URP
{
    /// <summary>
    /// Holds the three Render Graph pass classes that make up the VRSL URP DMX
    /// realtime-light pipeline:
    ///
    ///   1. GridTexturePass — publishes the DMX grid textures to the fixture-body
    ///      shaders from inside the graph. The decode itself (VRSLDMXLightUpdate.compute
    ///      writing the VRSLLightData buffer) is camera-independent and the manager
    ///      dispatches it once per frame, before the first camera's passes record.
    ///
    ///   2. LightingPass — fullscreen additive pass; reconstructs world position
    ///      from depth, derives a per-pixel normal from screen-space derivatives
    ///      of that position, and adds each GPU-decoded light's contribution to
    ///      the frame (Hidden/VRSL-URP/DeferredLighting shader).
    ///
    ///   3. VolumetricPass — three Render Graph sub-passes that depth-downsample,
    ///      raymarch in-scattering at half resolution, and bilaterally composite
    ///      the result onto the camera colour target (Hidden/VRSL-URP/VolumetricLighting).
    ///
    /// VRSL_URPLightManager subscribes to RenderPipelineManager.beginCameraRendering
    /// and enqueues instances of these passes per camera. There is no
    /// ScriptableRendererFeature in this pipeline — the runtime-injection path is
    /// the only supported mode of operation, so no URP Renderer asset authoring is
    /// required.
    /// </summary>
    public static class VRSLDMXLightPasses
    {
        // ── Grid textures: published for the opaque pass ──────────────────────
        /// <summary>
        /// Makes the DMX grid CRTs visible to the fixture-body surface shaders.
        /// </summary>
        /// <remarks>
        /// Published from inside the graph via <c>SetGlobalTextureAfterPass</c> rather
        /// than by <c>Shader.SetGlobalTexture</c> on the manager: a global texture set
        /// outside Render Graph binds to scene shaders as a 1x1 black fallback, so the
        /// surface decoded a black grid (every bar dark) while the compute, which bound
        /// the texture itself, read the real data. Global vectors such as
        /// <c>_VRSLDMXTexelSize</c> do not suffer the same fallback.
        ///
        /// A pass with no attachments and nothing to draw, kept for the globals it
        /// sets, which is how URP publishes its own. It writes nothing the graph can
        /// see consumed, so it opts out of pass culling.
        /// </remarks>
        public class GridTexturePass : ScriptableRenderPass
        {
            class PassData { }

            static readonly int s_DMXGrid         = Shader.PropertyToID("_VRSLU_DMXGridRenderTexture");
            static readonly int s_DMXGridMovement = Shader.PropertyToID("_VRSLU_DMXGridRenderTextureMovement");
            static readonly int s_DMXGridStrobe   = Shader.PropertyToID("_VRSLU_DMXGridStrobeOutput");
            static readonly int s_DMXGridSpin     = Shader.PropertyToID("_VRSLU_DMXGridSpinTimer");

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var mgr = VRSL_URPLightManager.Instance;
                if (mgr == null || mgr.FixtureCount == 0 || mgr.DMXMainHandle == null) return;

                using var builder = rg.AddRasterRenderPass<PassData>("VRSL DMX Grid Textures", out _);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                Publish(rg, builder, mgr.DMXMainHandle,      s_DMXGrid);
                Publish(rg, builder, mgr.DMXMovementHandle,  s_DMXGridMovement);
                Publish(rg, builder, mgr.DMXStrobeHandle,    s_DMXGridStrobe);
                Publish(rg, builder, mgr.DMXSpinTimerHandle, s_DMXGridSpin);

                builder.SetRenderFunc(static (PassData p, RasterGraphContext ctx) => { });
            }

            static void Publish(RenderGraph rg, IRasterRenderGraphBuilder builder, RTHandle handle, int id)
            {
                if (handle == null) return;
                var tex = rg.ImportTexture(handle);
                builder.UseTexture(tex, AccessFlags.Read);
                builder.SetGlobalTextureAfterPass(tex, id);
            }
        }

        // ── Fullscreen additive pass: light the scene ──────────────────────────
        public class LightingPass : ScriptableRenderPass
        {
            class PassData
            {
                public BufferHandle  lightDataBuffer;
                public BufferHandle  tileLightIndices;
                public TextureHandle depthTexture;
                public Material      material;
                public int           lightCount;
                public Vector4       tileParams;
                public bool          bindTileBuffer;
                public bool          surfaceDataValid;
                public Vector4       contactShadowParams;
            }

            /// <summary>The level this camera's pass costs at. Set by the manager
            /// per camera before enqueue; a mirror under the Reduced policy gets a
            /// lower one than the player's view.</summary>
            public VRSLQuality Quality = VRSLQuality.Standard;

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var mgr = VRSL_URPLightManager.Instance;
                if (mgr == null || mgr.FixtureCount == 0
                    || mgr.LightingMaterial == null
                    || mgr.LightDataBuffer  == null) return;

                var resources = frame.Get<UniversalResourceData>();

                if (!resources.cameraDepthTexture.IsValid())
                {
                    Debug.LogWarning("[VRSL] URP lighting requires the camera depth texture; "
                        + "enable Depth Texture on the active URP asset.");
                    return;
                }

                using var builder = rg.AddRasterRenderPass<PassData>("VRSL Lighting Pass", out var d);

                // The tile cull records earlier in this graph, so its results are
                // available here. When it did not run, tileParams stays zero and
                // the shader falls back to iterating every light — but the buffer
                // is still bound, since leaving the slot empty is not uniformly
                // safe across graphics APIs.
                var cull    = mgr.TileCullPass;
                var binding = cull != null ? cull.GetBinding() : default;
                d.bindTileBuffer = binding.Bind;
                d.tileParams     = binding.TileParams;

                d.lightDataBuffer = rg.ImportBuffer(mgr.LightDataBuffer);
                d.depthTexture    = resources.cameraDepthTexture;
                d.material        = mgr.LightingMaterial;
                d.lightCount      = mgr.FixtureCount;
                d.surfaceDataValid = mgr.surfacePropertiesShader != null;
                d.contactShadowParams = mgr.ContactShadowParamsFor(VRSLQualityLevel.For(Quality));

                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.UseBuffer( d.lightDataBuffer, AccessFlags.Read);
                builder.UseTexture(d.depthTexture,    AccessFlags.Read);
                if (d.bindTileBuffer)
                {
                    d.tileLightIndices = rg.ImportBuffer(cull.TileBuffer);
                    builder.UseBuffer(d.tileLightIndices, AccessFlags.Read);
                }
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData p, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetGlobalBuffer( "_VRSLLights",     p.lightDataBuffer);
                    cmd.SetGlobalInteger("_VRSLLightCount", p.lightCount);
                    cmd.SetGlobalVector( "_VRSLTileParams", p.tileParams);
                    cmd.SetGlobalFloat(  "_VRSLSurfaceDataValid", p.surfaceDataValid ? 1f : 0f);
                    cmd.SetGlobalVector( "_VRSLContactShadowParams", p.contactShadowParams);
                    if (p.bindTileBuffer)
                        cmd.SetGlobalBuffer("_VRSLTileLightIndices", p.tileLightIndices);
                    // Full-screen triangle: 3 vertices, no vertex buffer needed
                    // RenderingUtils.fullscreenMesh + cmd.DrawMesh is the URP-recommended
                    // pattern for fullscreen passes (cmd.DrawProcedural and cmd.Blit have
                    // known XR-integration issues in URP); URP's XR system applies
                    // SetInstanceMultiplier(viewCount) so the per-eye instance flow Just
                    // Works through the SPI macros in the shader.
                    cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity,
                        p.material, 0, 0);
                });
            }
        }

        // ── Volumetric pass: raymarched in-scattering ──────────────────────────
        // Records three Render Graph sub-passes. Half-res transient RTs are
        // created with rg.CreateTexture so they live exactly for this frame.
        public class VolumetricPass : ScriptableRenderPass
        {
            class DownsampleData
            {
                public Material      material;
                public TextureHandle fullDepth;
            }

            class RaymarchData
            {
                public Material      material;
                public TextureHandle halfDepth;
                public BufferHandle  lightDataBuffer;
                public BufferHandle  tileLightIndices;
                public int           lightCount;
                public Vector4       stepParams;
                public Vector4       densityParams;
                public Vector4       fogTintParams;
                public Vector4       tileParams;
                public bool          bindTileBuffer;
                public BufferHandle  statsBuffer;
                public bool          bindStats;
                public bool          collectStats;
                public float         time;
            }

            class UpsampleData
            {
                public Material      material;
                public TextureHandle halfRT;
                public TextureHandle halfDepth;
                public Vector4       halfResSize;
            }

            /// <summary>The level this camera's march costs at. Set by the manager
            /// per camera before enqueue; a mirror under the Reduced policy gets a
            /// lower one than the player's view.</summary>
            public VRSLQuality Quality = VRSLQuality.Standard;

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var mgr   = VRSL_URPLightManager.Instance;
                var level = VRSLQualityLevel.For(Quality);
                if (mgr == null
                    || mgr.FixtureCount == 0
                    || !level.Volumetrics
                    || mgr.VolumetricMaterial == null
                    || mgr.LightDataBuffer == null) return;

                if (level.VolumetricNoise)
                    mgr.VolumetricMaterial.EnableKeyword("_VRSL_VOLUMETRIC_NOISE");
                else
                    mgr.VolumetricMaterial.DisableKeyword("_VRSL_VOLUMETRIC_NOISE");

                var resources = frame.Get<UniversalResourceData>();
                var camData   = frame.Get<UniversalCameraData>();

                if (!resources.cameraDepthTexture.IsValid()) return;

                BufferHandle lightDataHandle = rg.ImportBuffer(mgr.LightDataBuffer);

                // Same tile list the surface pass uses. The view ray for a pixel
                // stays inside its screen tile, and the tile frusta cover the
                // camera's full depth range, so one lookup serves every step.
                var  cull         = mgr.TileCullPass;
                var binding         = cull != null ? cull.GetBinding() : default;
                bool bindTileBuffer = binding.Bind;
                Vector4 tileParams  = binding.TileParams;
                BufferHandle tileHandle = bindTileBuffer
                    ? rg.ImportBuffer(cull.TileBuffer)
                    : default;

                int halfW = Mathf.Max(1, camData.cameraTargetDescriptor.width  / 2);
                int halfH = Mathf.Max(1, camData.cameraTargetDescriptor.height / 2);

                // Single-pass-instanced VR runs both eyes through one camera with
                // a 2-slice render target (volumeDepth = 2). Allocate the half-res
                // transient RTs as Tex2DArrays with matching slices so each eye
                // raymarchs against its own depth and the upsample reads the
                // correct slice for its eye — without this, both eyes share a
                // single buffer and the volumetric beam reads from the other
                // eye's view (visible as the cone being translated out of place
                // in one eye).
                int sliceCount = Mathf.Max(1, camData.cameraTargetDescriptor.volumeDepth);
                TextureDimension halfResDim = sliceCount > 1
                    ? TextureDimension.Tex2DArray
                    : TextureDimension.Tex2D;

                var halfDepthDesc = new TextureDesc(halfW, halfH)
                {
                    name        = "VRSL Volumetric Half Depth",
                    format      = GraphicsFormat.R32_SFloat,
                    clearBuffer = false,
                    filterMode  = FilterMode.Point,
                    dimension   = halfResDim,
                    slices      = sliceCount,
                };
                var halfRTDesc = new TextureDesc(halfW, halfH)
                {
                    name        = "VRSL Volumetric Half RT",
                    format      = GraphicsFormat.R16G16B16A16_SFloat,
                    clearBuffer = true,
                    clearColor  = Color.clear,
                    filterMode  = FilterMode.Point,
                    dimension   = halfResDim,
                    slices      = sliceCount,
                };
                TextureHandle halfDepth = rg.CreateTexture(halfDepthDesc);
                TextureHandle halfRT    = rg.CreateTexture(halfRTDesc);

                Vector4 halfResSize = new Vector4(halfW, halfH, 1f / halfW, 1f / halfH);

                // Sub-pass 1 — depth downsample
                using (var builder = rg.AddRasterRenderPass<DownsampleData>(
                    "VRSL Vol Depth Downsample", out var d))
                {
                    d.material  = mgr.VolumetricMaterial;
                    d.fullDepth = resources.cameraDepthTexture;

                    builder.SetRenderAttachment(halfDepth, 0, AccessFlags.Write);
                    builder.UseTexture(d.fullDepth, AccessFlags.Read);

                    builder.SetRenderFunc((DownsampleData p, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity,
                            p.material, 0, 0);
                    });
                }

                // Sub-pass 2 — raymarch
                using (var builder = rg.AddRasterRenderPass<RaymarchData>(
                    "VRSL Vol Raymarch", out var d))
                {
                    d.material        = mgr.VolumetricMaterial;
                    d.halfDepth       = halfDepth;
                    d.lightDataBuffer = lightDataHandle;
                    d.lightCount      = mgr.FixtureCount;
                    d.stepParams      = mgr.VolumetricStepParamsFor(level);
                    d.densityParams   = mgr.VolumetricDensityParams;
                    d.fogTintParams   = mgr.VolumetricFogTintParams;
                    d.bindTileBuffer  = bindTileBuffer;
                    d.tileParams      = tileParams;
                    d.tileLightIndices = tileHandle;
                    // Bound whenever it exists, collecting or not, so the UAV slot
                    // the shader declares is never left unbound.
                    var stats         = mgr.VolumetricStats;
                    d.bindStats       = stats.Buffer != null;
                    d.collectStats    = d.bindStats && stats.Collecting;
                    d.statsBuffer     = d.bindStats ? rg.ImportBuffer(stats.Buffer) : default;
                    d.time            = mgr.VolumetricTime;

                    builder.SetRenderAttachment(halfRT, 0, AccessFlags.Write);
                    builder.UseTexture(d.halfDepth, AccessFlags.Read);
                    builder.UseBuffer(d.lightDataBuffer, AccessFlags.Read);
                    if (bindTileBuffer)
                        builder.UseBuffer(d.tileLightIndices, AccessFlags.Read);
                    if (d.bindStats)
                        builder.UseBufferRandomAccess(d.statsBuffer, 1, AccessFlags.ReadWrite);
                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc((RaymarchData p, RasterGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        cmd.SetGlobalBuffer( "_VRSLLights",            p.lightDataBuffer);
                        cmd.SetGlobalInteger("_VRSLLightCount",        p.lightCount);
                        cmd.SetGlobalTexture("_VRSLVolHalfResDepth",   p.halfDepth);
                        cmd.SetGlobalVector( "_VRSLVolStepCount",      p.stepParams);
                        cmd.SetGlobalVector( "_VRSLVolDensity",        p.densityParams);
                        cmd.SetGlobalVector( "_VRSLVolFogTint",        p.fogTintParams);
                        cmd.SetGlobalVector( "_VRSLTileParams",        p.tileParams);
                        if (p.bindTileBuffer)
                            cmd.SetGlobalBuffer("_VRSLTileLightIndices", p.tileLightIndices);
                        cmd.SetGlobalInteger("_VRSLVolCollectStats",   p.collectStats ? 1 : 0);
                        cmd.SetGlobalFloat(  "_VRSLVolTime",           p.time);
                        cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity,
                            p.material, 0, 1);
                    });
                }

                // Sub-pass 3 — bilateral upsample composite onto camera colour
                using (var builder = rg.AddRasterRenderPass<UpsampleData>(
                    "VRSL Vol Upsample", out var d))
                {
                    d.material    = mgr.VolumetricMaterial;
                    d.halfRT      = halfRT;
                    d.halfDepth   = halfDepth;
                    d.halfResSize = halfResSize;

                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.UseTexture(d.halfRT,    AccessFlags.Read);
                    builder.UseTexture(d.halfDepth, AccessFlags.Read);
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc((UpsampleData p, RasterGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        cmd.SetGlobalTexture("_VRSLVolumetricRT",    p.halfRT);
                        cmd.SetGlobalTexture("_VRSLVolHalfResDepth", p.halfDepth);
                        // Provide half-res dimensions explicitly — the shader
                        // can't use Texture2D.GetDimensions on a TEXTURE2D_X
                        // (which resolves to Texture2DArray under SPI VR).
                        cmd.SetGlobalVector ("_VRSLVolHalfResSize",  p.halfResSize);
                        cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity,
                            p.material, 0, 2);
                    });
                }
            }
        }

    }
}

#pragma warning restore 618
