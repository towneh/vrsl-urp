using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace VRSL.URP
{
    /// <summary>
    /// Holds the three Render Graph pass classes that make up the VRSL URP DMX
    /// realtime-light pipeline:
    ///
    ///   1. ComputePass — dispatches VRSLDMXLightUpdate.compute, which reads the
    ///      three DMX RenderTextures and writes a VRSLLightData StructuredBuffer.
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
        // ── Compute pass: decode DMX → light buffer ────────────────────────────
        public class ComputePass : ScriptableRenderPass
        {
            class PassData
            {
                public BufferHandle  fixtureConfigBuffer;
                public BufferHandle  lightDataBuffer;
                public TextureHandle dmxMainTex;
                public TextureHandle dmxMovementTex;
                public TextureHandle dmxStrobeTex;
                public TextureHandle dmxSpinTimerTex;
                public ComputeShader cs;
                public int           kernel;
                public int           fixtureCount;
                public int           goboCount;
                public Vector4       texelSize;
            }

            // Grid CRTs published to the fixture-body surface shaders from inside this pass
            // (which runs BeforeRenderingOpaques) via SetGlobalTextureAfterPass — NOT via
            // Shader.SetGlobalTexture on the manager. A global texture set outside RenderGraph
            // binds to scene shaders as a 1x1 black fallback, so the surface decoded a black
            // grid (every bar dark) while the compute, which binds the handle in-pass, read the
            // real data. Global vectors (e.g. _VRSLDMXTexelSize) don't suffer the same fallback.
            static readonly int s_DMXGrid         = Shader.PropertyToID("_VRSLU_DMXGridRenderTexture");
            static readonly int s_DMXGridMovement = Shader.PropertyToID("_VRSLU_DMXGridRenderTextureMovement");
            static readonly int s_DMXGridStrobe   = Shader.PropertyToID("_VRSLU_DMXGridStrobeOutput");
            static readonly int s_DMXGridSpin     = Shader.PropertyToID("_VRSLU_DMXGridSpinTimer");

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var mgr = VRSL_URPLightManager.Instance;
                if (mgr == null || mgr.FixtureCount == 0
                    || mgr.computeShader == null
                    || mgr.FixtureConfigBuffer == null
                    || mgr.DMXMainHandle == null) return;

                using var builder = rg.AddComputePass<PassData>("VRSL DMX Light Compute", out var d);

                // URP's RenderGraph would otherwise cull this pass under stereo XR
                // because _VRSLLights is consumed by the lighting/volumetric shaders
                // via SetGlobalBuffer rather than a tracked read on the same handle,
                // so the graph sees the compute write as a dead store. The compute
                // is always wanted whenever fixtures exist, so opt out of culling.
                builder.AllowPassCulling(false);

                d.fixtureConfigBuffer = rg.ImportBuffer(mgr.FixtureConfigBuffer);
                d.lightDataBuffer     = rg.ImportBuffer(mgr.LightDataBuffer);
                d.dmxMainTex          = rg.ImportTexture(mgr.DMXMainHandle);
                d.dmxMovementTex      = mgr.DMXMovementHandle != null
                    ? rg.ImportTexture(mgr.DMXMovementHandle)
                    : TextureHandle.nullHandle;
                d.dmxStrobeTex        = mgr.DMXStrobeHandle != null
                    ? rg.ImportTexture(mgr.DMXStrobeHandle)
                    : TextureHandle.nullHandle;
                d.dmxSpinTimerTex     = mgr.DMXSpinTimerHandle != null
                    ? rg.ImportTexture(mgr.DMXSpinTimerHandle)
                    : TextureHandle.nullHandle;

                d.cs           = mgr.computeShader;
                d.kernel       = mgr.ComputeKernel;
                d.fixtureCount = mgr.FixtureCount;
                d.goboCount    = mgr.GoboCount;
                d.texelSize    = new Vector4(
                    1f / mgr.dmxMainTexture.width,
                    1f / mgr.dmxMainTexture.height,
                    mgr.dmxMainTexture.width,
                    mgr.dmxMainTexture.height);

                builder.UseBuffer(d.fixtureConfigBuffer, AccessFlags.Read);
                builder.UseBuffer(d.lightDataBuffer,     AccessFlags.Write);
                builder.UseTexture(d.dmxMainTex,         AccessFlags.Read);
                if (d.dmxMovementTex.IsValid())
                    builder.UseTexture(d.dmxMovementTex, AccessFlags.Read);
                if (d.dmxStrobeTex.IsValid())
                    builder.UseTexture(d.dmxStrobeTex,   AccessFlags.Read);
                if (d.dmxSpinTimerTex.IsValid())
                    builder.UseTexture(d.dmxSpinTimerTex, AccessFlags.Read);

                // Make the grid CRTs visible to the fixture-body surface shaders (opaque pass).
                builder.SetGlobalTextureAfterPass(d.dmxMainTex, s_DMXGrid);
                if (d.dmxMovementTex.IsValid())
                    builder.SetGlobalTextureAfterPass(d.dmxMovementTex, s_DMXGridMovement);
                if (d.dmxStrobeTex.IsValid())
                    builder.SetGlobalTextureAfterPass(d.dmxStrobeTex,   s_DMXGridStrobe);
                if (d.dmxSpinTimerTex.IsValid())
                    builder.SetGlobalTextureAfterPass(d.dmxSpinTimerTex, s_DMXGridSpin);

                builder.SetRenderFunc((PassData p, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetComputeVectorParam( p.cs,           "_VRSLDMXTexelSize", p.texelSize);
                    cmd.SetComputeIntParam(    p.cs,           "_FixtureCount",     p.fixtureCount);
                    cmd.SetComputeIntParam(    p.cs,           "_VRSLGoboCount",    p.goboCount);
                    cmd.SetComputeBufferParam( p.cs, p.kernel, "_FixtureConfigs",   p.fixtureConfigBuffer);
                    cmd.SetComputeBufferParam( p.cs, p.kernel, "_LightData",        p.lightDataBuffer);
                    cmd.SetComputeTextureParam(p.cs, p.kernel, "_DMXMainTex",       p.dmxMainTex);

                    if (p.dmxMovementTex.IsValid())
                        cmd.SetComputeTextureParam(p.cs, p.kernel, "_DMXMovementTex", p.dmxMovementTex);
                    if (p.dmxStrobeTex.IsValid())
                        cmd.SetComputeTextureParam(p.cs, p.kernel, "_DMXStrobeTex",   p.dmxStrobeTex);
                    if (p.dmxSpinTimerTex.IsValid())
                        cmd.SetComputeTextureParam(p.cs, p.kernel, "_DMXSpinTimerTex", p.dmxSpinTimerTex);

                    cmd.DispatchCompute(p.cs, p.kernel, Mathf.CeilToInt(p.fixtureCount / 64f), 1, 1);
                });
            }
        }

        // ── Fullscreen additive pass: light the scene ──────────────────────────
        public class LightingPass : ScriptableRenderPass
        {
            class PassData
            {
                public BufferHandle  lightDataBuffer;
                public TextureHandle depthTexture;
                public TextureHandle opaqueSnapshot;
                public Material      material;
                public int           lightCount;
                public float         albedoTintStrength;
            }

            class CapturePassData
            {
                public TextureHandle source;
                public Material      material;
            }

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

                // Capture sub-pass — only when the albedo tint is active. URP 17's
                // own _CameraOpaqueTexture isn't reliable for our injection point
                // under render graph (CopyColor doesn't always run, even with
                // Opaque Texture enabled on the URP asset and ConfigureInput(Color)
                // requested), so the package captures its own snapshot of the
                // pre-VRSL camera colour into a transient render-graph texture
                // and binds it as _VRSLOpaqueTexture for the lighting sub-pass.
                bool wantTint = mgr.albedoTintStrength > 0f;
                TextureHandle opaqueSnapshot = TextureHandle.nullHandle;
                if (wantTint)
                {
                    var camData = frame.Get<UniversalCameraData>();
                    var camDesc = camData.cameraTargetDescriptor;
                    int sliceCount = Mathf.Max(1, camDesc.volumeDepth);
                    var snapDesc = new TextureDesc(camDesc.width, camDesc.height)
                    {
                        name        = "VRSL Opaque Snapshot",
                        format      = camDesc.graphicsFormat,
                        clearBuffer = false,
                        filterMode  = FilterMode.Bilinear,
                        dimension   = sliceCount > 1
                                          ? TextureDimension.Tex2DArray
                                          : TextureDimension.Tex2D,
                        slices      = sliceCount,
                    };
                    opaqueSnapshot = rg.CreateTexture(snapDesc);

                    using var capture = rg.AddRasterRenderPass<CapturePassData>(
                        "VRSL Opaque Capture", out var cd);
                    cd.source   = resources.activeColorTexture;
                    cd.material = mgr.LightingMaterial;
                    capture.SetRenderAttachment(opaqueSnapshot, 0, AccessFlags.Write);
                    capture.UseTexture(cd.source, AccessFlags.Read);
                    capture.AllowGlobalStateModification(true);
                    capture.SetRenderFunc((CapturePassData p, RasterGraphContext ctx) =>
                    {
                        var cmd = ctx.cmd;
                        cmd.SetGlobalTexture("_VRSLBlitSource", p.source);
                        cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity,
                            p.material, 0, 1);
                    });
                }

                using var builder = rg.AddRasterRenderPass<PassData>("VRSL Lighting Pass", out var d);

                d.lightDataBuffer    = rg.ImportBuffer(mgr.LightDataBuffer);
                d.depthTexture       = resources.cameraDepthTexture;
                d.opaqueSnapshot     = opaqueSnapshot;
                d.material           = mgr.LightingMaterial;
                d.lightCount         = mgr.FixtureCount;
                d.albedoTintStrength = wantTint ? mgr.albedoTintStrength : 0f;

                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.UseBuffer( d.lightDataBuffer, AccessFlags.Read);
                builder.UseTexture(d.depthTexture,    AccessFlags.Read);
                if (wantTint)
                    builder.UseTexture(opaqueSnapshot, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData p, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetGlobalBuffer( "_VRSLLights",      p.lightDataBuffer);
                    cmd.SetGlobalInteger("_VRSLLightCount",  p.lightCount);
                    cmd.SetGlobalFloat(  "_VRSLAlbedoTint",  p.albedoTintStrength);
                    if (p.albedoTintStrength > 0f)
                        cmd.SetGlobalTexture("_VRSLOpaqueTexture", p.opaqueSnapshot);
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
                public int           lightCount;
                public Vector4       stepParams;
                public Vector4       densityParams;
                public Vector4       fogTintParams;
            }

            class UpsampleData
            {
                public Material      material;
                public TextureHandle halfRT;
                public TextureHandle halfDepth;
                public Vector4       halfResSize;
            }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var mgr = VRSL_URPLightManager.Instance;
                if (mgr == null
                    || mgr.FixtureCount == 0
                    || mgr.VolumetricMaterial == null
                    || mgr.LightDataBuffer == null) return;

                if (mgr.VolumetricUseNoise)
                    mgr.VolumetricMaterial.EnableKeyword("_VRSL_VOLUMETRIC_NOISE");
                else
                    mgr.VolumetricMaterial.DisableKeyword("_VRSL_VOLUMETRIC_NOISE");

                var resources = frame.Get<UniversalResourceData>();
                var camData   = frame.Get<UniversalCameraData>();

                if (!resources.cameraDepthTexture.IsValid()) return;

                BufferHandle lightDataHandle = rg.ImportBuffer(mgr.LightDataBuffer);

                if (mgr.VolumetricUseFullRes)
                {
                    // Full-res path — single raymarch pass that samples the full
                    // depth texture and additive-blends onto the camera colour.
                    // Skips the depth downsample and bilateral upsample passes.
                    using (var builder = rg.AddRasterRenderPass<RaymarchData>(
                        "VRSL Vol Raymarch FullRes", out var d))
                    {
                        d.material        = mgr.VolumetricMaterial;
                        d.halfDepth       = TextureHandle.nullHandle;
                        d.lightDataBuffer = lightDataHandle;
                        d.lightCount      = mgr.FixtureCount;
                        d.stepParams      = mgr.VolumetricStepParams;
                        d.densityParams   = mgr.VolumetricDensityParams;
                        d.fogTintParams   = mgr.VolumetricFogTintParams;

                        builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                        builder.UseBuffer(d.lightDataBuffer, AccessFlags.Read);
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                        builder.AllowGlobalStateModification(true);

                        builder.SetRenderFunc((RaymarchData p, RasterGraphContext ctx) =>
                        {
                            var cmd = ctx.cmd;
                            cmd.SetGlobalBuffer( "_VRSLLights",       p.lightDataBuffer);
                            cmd.SetGlobalInteger("_VRSLLightCount",   p.lightCount);
                            cmd.SetGlobalVector( "_VRSLVolStepCount", p.stepParams);
                            cmd.SetGlobalVector( "_VRSLVolDensity",   p.densityParams);
                            cmd.SetGlobalVector( "_VRSLVolFogTint",   p.fogTintParams);
                            cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity,
                                p.material, 0, 3);
                        });
                    }
                    return;
                }

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
                    d.stepParams      = mgr.VolumetricStepParams;
                    d.densityParams   = mgr.VolumetricDensityParams;
                    d.fogTintParams   = mgr.VolumetricFogTintParams;

                    builder.SetRenderAttachment(halfRT, 0, AccessFlags.Write);
                    builder.UseTexture(d.halfDepth, AccessFlags.Read);
                    builder.UseBuffer(d.lightDataBuffer, AccessFlags.Read);
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
