using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

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
    /// The volumetric settings a manager exposes to the shared passes. Both
    /// managers carry the same fields; this lets one pass class serve either.
    /// </summary>
    public interface IVRSLVolumetricSource : IVRSLLightSource
    {
        Material VolumetricMaterial { get; }
        Vector4  VolumetricStepParams { get; }
        Vector4  VolumetricDensityParams { get; }
        Vector4  VolumetricFogTintParams { get; }
        bool     VolumetricUseNoise { get; }
        bool     VolumetricCoupleToSceneFog { get; }
        VRSLTileCullPass TileCullPass { get; }

        /// <summary>Compute driving the froxel scatter and integrate kernels.</summary>
        ComputeShader FroxelShader { get; }

        /// <summary>Volume dimensions in froxels, per eye.</summary>
        Vector3Int FroxelResolution { get; }

        /// <summary>How far from the camera the volume reaches, in metres.</summary>
        float FroxelMaxDistance { get; }
    }

    /// <summary>
    /// Integrates volumetric scattering into a view-aligned 3D grid once per
    /// camera, so the composite is a single texture fetch per pixel.
    ///
    /// The raymarch path re-integrates every view ray per screen pixel, running
    /// the light loop once per step — its cost is tied to framebuffer resolution,
    /// which is the wrong thing to scale with in VR. This does the same work
    /// against a fixed grid instead, so cost tracks the volume's dimensions.
    /// Trilinear sampling of the result also removes the raymarch's dependence on
    /// per-pixel jitter to hide banding.
    ///
    /// Slices are distributed exponentially between the near plane and
    /// <see cref="IVRSLVolumetricSource.FroxelMaxDistance"/>, concentrating
    /// resolution near the camera. Scattering past that distance is not
    /// represented — the volume is a near- and mid-field approximation, not a
    /// replacement for the raymarch at every scale.
    ///
    /// Both eyes share one volume, packed along X, since a 3D texture cannot be
    /// a texture array.
    /// </summary>
    public class VRSLFroxelPass : ScriptableRenderPass
    {
        static readonly int s_VolumeID      = Shader.PropertyToID("_VRSLFroxelVolume");
        static readonly int s_ParamsID      = Shader.PropertyToID("_VRSLFroxelParams");
        static readonly int s_ViewParamsID  = Shader.PropertyToID("_VRSLFroxelViewParams");
        static readonly int s_CamPosID      = Shader.PropertyToID("_VRSLFroxelCamPos");
        static readonly int s_CamFwdID      = Shader.PropertyToID("_VRSLFroxelCamFwd");
        static readonly int s_InvViewProjID = Shader.PropertyToID("_VRSLFroxelInvViewProj0");
        static readonly int s_InvViewProj1ID = Shader.PropertyToID("_VRSLFroxelInvViewProj1");
        static readonly int s_TimeID        = Shader.PropertyToID("_VRSLFroxelTime");
        static readonly int s_LightsID      = Shader.PropertyToID("_VRSLLights");
        static readonly int s_LightCountID  = Shader.PropertyToID("_VRSLLightCount");
        static readonly int s_TileIndicesID = Shader.PropertyToID("_VRSLTileLightIndices");
        static readonly int s_TileParamsID  = Shader.PropertyToID("_VRSLTileParams");
        static readonly int s_StepParamsID  = Shader.PropertyToID("_VRSLVolStepCount");
        static readonly int s_DensityID     = Shader.PropertyToID("_VRSLVolDensity");
        static readonly int s_FogTintID     = Shader.PropertyToID("_VRSLVolFogTint");

        const string NoiseKeyword = "_VRSL_VOLUMETRIC_NOISE";

        readonly IVRSLVolumetricSource _source;

        int _scatterKernel   = -1;
        int _integrateKernel = -1;

        public VRSLFroxelPass(IVRSLVolumetricSource source)
        {
            _source = source;

            var cs = source?.FroxelShader;
            if (cs != null)
            {
                if (cs.HasKernel("ScatterFroxels"))   _scatterKernel   = cs.FindKernel("ScatterFroxels");
                if (cs.HasKernel("IntegrateFroxels")) _integrateKernel = cs.FindKernel("IntegrateFroxels");
            }

            renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.AfterRenderingOpaques + 1);
        }

        public bool IsUsable => _scatterKernel >= 0 && _integrateKernel >= 0;

        const int MinFroxelAxis   = 8;
        const int MaxFroxelAxisXY = 256;
        const int MaxFroxelAxisZ  = 128;

        /// <summary>
        /// Bounds a requested volume size to what can actually be allocated.
        /// [Range] can't apply per-component to a Vector3Int, and a zero or
        /// negative axis produces an invalid TextureDesc and a divide by zero in
        /// the compute's slice distribution. Public so diagnostics can report
        /// what will really render rather than what was typed.
        /// </summary>
        public static Vector3Int ClampResolution(Vector3Int requested) =>
            new Vector3Int(
                Mathf.Clamp(requested.x, MinFroxelAxis, MaxFroxelAxisXY),
                Mathf.Clamp(requested.y, MinFroxelAxis, MaxFroxelAxisXY),
                Mathf.Clamp(requested.z, MinFroxelAxis, MaxFroxelAxisZ));

        class ScatterData
        {
            public ComputeShader cs;
            public int           scatterKernel;
            public int           integrateKernel;
            public TextureHandle volume;
            public BufferHandle  lightData;
            public BufferHandle  tileIndices;
            public bool          bindTileBuffer;
            public int           lightCount;
            public Vector4       froxelParams;
            public Vector4       viewParams;
            public Vector4       camPos;
            public Vector4       camFwd;
            public Vector4       tileParams;
            public Vector4       stepParams;
            public Vector4       densityParams;
            public Matrix4x4[]   invViewProj;
            public float         time;
            public Vector3Int    dims;
            public int           views;
        }

        class CompositeData
        {
            public Material      material;
            public TextureHandle volume;
            public Vector4       froxelParams;
            public Vector4       viewParams;
            public Vector4       fogTintParams;
        }

        public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
        {
            if (!IsUsable) return;
            if (_source == null || _source.FixtureCount == 0
                || _source.LightDataBuffer  == null
                || _source.VolumetricMaterial == null) return;

            var camData   = frame.Get<UniversalCameraData>();
            var resources = frame.Get<UniversalResourceData>();
            if (!resources.cameraDepthTexture.IsValid()) return;

            var dims  = ClampResolution(_source.FroxelResolution);
            int views = Mathf.Clamp(camData.cameraTargetDescriptor.volumeDepth, 1, 2);

            var volumeDesc = new TextureDesc(dims.x * views, dims.y)
            {
                name        = "_VRSLFroxelVolume",
                format      = GraphicsFormat.R16G16B16A16_SFloat,
                dimension   = TextureDimension.Tex3D,
                slices      = dims.z,
                enableRandomWrite = true,
                // Deliberately not cleared. RenderGraph applies clearBuffer when a
                // texture is first used as a render attachment, and this one is
                // only ever a compute UAV — so the clear's ordering against the
                // dispatches isn't guaranteed and can land after them, leaving the
                // volume zeroed and the effect silently absent. The scatter kernel
                // writes every in-bounds froxel and nothing reads out-of-bounds
                // ones, so the contents are fully defined without it. The
                // composite guards against NaN instead.
                clearBuffer = false,
                filterMode  = FilterMode.Bilinear,
                wrapMode    = TextureWrapMode.Clamp,
            };
            TextureHandle volume = rg.CreateTexture(volumeDesc);

            // Same convention as the tile cull and the fullscreen shaders, so the
            // volume lines up with the depth buffer the composite samples.
            bool renderIntoTexture = !resources.isActiveTargetBackBuffer;
            // Allocated per record — see the matching note in VRSLTileCullPass.
            var invViewProj = new Matrix4x4[2];
            for (int view = 0; view < 2; view++)
            {
                int src = Mathf.Min(view, views - 1);
                Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(
                    camData.GetProjectionMatrix(src), renderIntoTexture);
                invViewProj[view] =
                    Matrix4x4.Inverse(camData.GetViewMatrix(src)) * Matrix4x4.Inverse(gpuProj);
            }

            // Set on the ComputeShader at record time rather than through the
            // command buffer. Keyword changes count as global state, which a
            // compute pass isn't allowed to make without opting in — and doing it
            // per-dispatch is the wrong place for what is asset-level state
            // anyway. The raymarch pass sets its material keyword the same way.
            var froxelCompute = _source.FroxelShader;
            if (_source.VolumetricUseNoise) froxelCompute.EnableKeyword(NoiseKeyword);
            else                            froxelCompute.DisableKeyword(NoiseKeyword);

            var cull    = _source.TileCullPass;
            var binding = cull != null ? cull.GetBinding() : default;

            // Scene-fog coupling, which the raymarch does in-shader from
            // unity_FogParams. Those built-ins aren't reliably bound to a compute,
            // so the same result is resolved here and folded into the density and
            // tint the pass already sends. Without this the toggle silently did
            // nothing in Froxel mode.
            var densityParams = _source.VolumetricDensityParams;
            var fogTint       = _source.VolumetricFogTintParams;
            if (_source.VolumetricCoupleToSceneFog)
            {
                // Matches the raymarch's unity_FogParams.x, which is the Exp2
                // coefficient regardless of the configured fog mode.
                float coefficient = RenderSettings.fog
                    ? RenderSettings.fogDensity / Mathf.Sqrt(Mathf.Log(2f))
                    : 0f;
                densityParams.x *= Mathf.Max(coefficient, 0f);

                Color fogColor = RenderSettings.fogColor;
                fogTint.x *= fogColor.r;
                fogTint.y *= fogColor.g;
                fogTint.z *= fogColor.b;
            }

            var froxelParams = new Vector4(dims.x, dims.y, dims.z, _source.FroxelMaxDistance);
            var viewParams   = new Vector4(views, camData.camera.nearClipPlane, 0f, 0f);

            // ── Scatter ──────────────────────────────────────────────────────
            // Split from the integrate below rather than issuing both dispatches
            // from one pass: the graph can only insert a UAV barrier between them
            // if it sees two passes touching the volume, and the integrate reads
            // exactly what the scatter wrote.
            using (var builder = rg.AddComputePass<ScatterData>("VRSL Froxel Scatter", out var d))
            {
                builder.AllowPassCulling(false);

                d.cs              = _source.FroxelShader;
                d.scatterKernel   = _scatterKernel;
                d.integrateKernel = _integrateKernel;
                d.volume          = volume;
                d.lightData       = rg.ImportBuffer(_source.LightDataBuffer);
                d.bindTileBuffer  = binding.Bind;
                d.lightCount      = _source.FixtureCount;
                d.froxelParams    = froxelParams;
                d.viewParams      = viewParams;
                d.camPos          = camData.camera.transform.position;
                d.camFwd          = camData.camera.transform.forward;
                d.tileParams      = binding.TileParams;
                d.stepParams      = _source.VolumetricStepParams;
                d.densityParams   = densityParams;
                d.invViewProj     = invViewProj;
                d.time            = Time.timeSinceLevelLoad;
                d.dims            = dims;
                d.views           = views;

                builder.UseTexture(volume, AccessFlags.Write);
                builder.UseBuffer(d.lightData, AccessFlags.Read);
                if (d.bindTileBuffer)
                {
                    d.tileIndices = rg.ImportBuffer(cull.TileBuffer);
                    builder.UseBuffer(d.tileIndices, AccessFlags.Read);
                }

                builder.SetRenderFunc((ScatterData p, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;

                    cmd.SetComputeVectorParam(     p.cs, s_ParamsID,      p.froxelParams);
                    cmd.SetComputeVectorParam(     p.cs, s_ViewParamsID,  p.viewParams);
                    cmd.SetComputeVectorParam(     p.cs, s_CamPosID,      p.camPos);
                    cmd.SetComputeVectorParam(     p.cs, s_CamFwdID,      p.camFwd);
                    cmd.SetComputeVectorParam(     p.cs, s_TileParamsID,  p.tileParams);
                    cmd.SetComputeVectorParam(     p.cs, s_StepParamsID,  p.stepParams);
                    cmd.SetComputeVectorParam(     p.cs, s_DensityID,     p.densityParams);
                    cmd.SetComputeMatrixParam(     p.cs, s_InvViewProjID,  p.invViewProj[0]);
                    cmd.SetComputeMatrixParam(     p.cs, s_InvViewProj1ID, p.invViewProj[1]);
                    cmd.SetComputeIntParam(        p.cs, s_LightCountID,  p.lightCount);
                    cmd.SetComputeFloatParam(      p.cs, s_TimeID,        p.time);

                    cmd.SetComputeTextureParam(p.cs, p.scatterKernel, s_VolumeID,      p.volume);
                    cmd.SetComputeBufferParam( p.cs, p.scatterKernel, s_LightsID,      p.lightData);
                    if (p.bindTileBuffer)
                        cmd.SetComputeBufferParam(p.cs, p.scatterKernel, s_TileIndicesID, p.tileIndices);

                    cmd.DispatchCompute(p.cs, p.scatterKernel,
                        Mathf.CeilToInt(p.dims.x * p.views / 4f),
                        Mathf.CeilToInt(p.dims.y / 4f),
                        Mathf.CeilToInt(p.dims.z / 4f));
                });
            }

            // ── Integrate ────────────────────────────────────────────────────
            using (var builder = rg.AddComputePass<ScatterData>("VRSL Froxel Integrate", out var d))
            {
                builder.AllowPassCulling(false);

                d.cs              = _source.FroxelShader;
                d.integrateKernel = _integrateKernel;
                d.volume          = volume;
                d.froxelParams    = froxelParams;
                d.viewParams      = viewParams;
                d.dims            = dims;
                d.views           = views;

                builder.UseTexture(volume, AccessFlags.ReadWrite);

                // Bind the finished volume for the composite through the graph
                // rather than SetGlobalTexture on the command buffer. A render
                // graph texture bound that way resolves to a 1x1 black fallback
                // in the consuming shader, which reads as "the effect silently
                // does nothing" — the same trap the DMX grid CRTs hit.
                builder.SetGlobalTextureAfterPass(volume, s_VolumeID);

                builder.SetRenderFunc((ScatterData p, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;

                    cmd.SetComputeVectorParam( p.cs, s_ParamsID,     p.froxelParams);
                    cmd.SetComputeVectorParam( p.cs, s_ViewParamsID, p.viewParams);
                    cmd.SetComputeTextureParam(p.cs, p.integrateKernel, s_VolumeID, p.volume);

                    // One thread per column; the kernel walks Z itself, because
                    // the integration is a running sum along that axis.
                    cmd.DispatchCompute(p.cs, p.integrateKernel,
                        Mathf.CeilToInt(p.dims.x * p.views / 8f),
                        Mathf.CeilToInt(p.dims.y / 8f),
                        1);
                });
            }

            // ── Composite ────────────────────────────────────────────────────
            using (var builder = rg.AddRasterRenderPass<CompositeData>(
                "VRSL Froxel Composite", out var d))
            {
                d.material      = _source.VolumetricMaterial;
                d.volume        = volume;
                d.froxelParams  = froxelParams;
                d.viewParams    = viewParams;
                d.fogTintParams = fogTint;

                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.UseTexture(volume, AccessFlags.Read);
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((CompositeData p, RasterGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    // _VRSLFroxelVolume is already bound by the integrate pass's
                    // SetGlobalTextureAfterPass; binding it here from the command
                    // buffer is what made it resolve to a 1x1 black fallback.
                    cmd.SetGlobalVector( s_ParamsID,     p.froxelParams);
                    cmd.SetGlobalVector( s_ViewParamsID, p.viewParams);
                    cmd.SetGlobalVector( s_FogTintID,    p.fogTintParams);
                    cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity,
                        p.material, 0, 4);
                });
            }
        }
    }
}

#pragma warning restore 618
