using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace VRSL.URP
{
    /// <summary>
    /// The light buffer a manager publishes for the shared render passes to
    /// consume. Implemented by both <see cref="VRSL_URPLightManager"/> (DMX) and
    /// <see cref="VRSL_AudioLinkURPLightManager"/> (AudioLink) — they write the
    /// same <c>VRSLLightData</c> layout, so the culling and lighting passes work
    /// against either without knowing which is driving them.
    /// </summary>
    public interface IVRSLLightSource
    {
        /// <summary>GPU buffer of decoded <c>VRSLLightData</c>, one entry per fixture.</summary>
        GraphicsBuffer LightDataBuffer { get; }

        /// <summary>Number of valid entries in <see cref="LightDataBuffer"/>.</summary>
        int FixtureCount { get; }
    }

    /// <summary>
    /// Builds a per-tile light index list so the fullscreen surface and
    /// volumetric passes iterate only the fixtures that actually reach each
    /// screen tile.
    ///
    /// Without this both passes loop every fixture in the scene on every pixel,
    /// and the volumetric pass repeats that loop once per raymarch step — the
    /// term that stops the pipeline scaling to large rigs. The tile frusta span
    /// the camera's full depth range rather than each tile's scene-depth bounds,
    /// which keeps one list valid for the volumetric ray (camera to surface) as
    /// well as the surface pass, and means the cull has no dependency on the
    /// depth texture being ready.
    ///
    /// Tiles are laid out (eye, y, x) so single-pass instanced VR gets an
    /// independent list per eye. The consuming passes bind
    /// <see cref="TileBuffer"/> and <see cref="TileParams"/> themselves rather
    /// than relying on a global set from inside this pass.
    /// </summary>
    public class VRSLTileCullPass : ScriptableRenderPass, IDisposable
    {
        /// <summary>Tile edge in pixels. Must match nothing in the shaders — the
        /// grid dimensions travel through <see cref="TileParams"/> — but smaller
        /// tiles cull tighter at the cost of more groups.</summary>
        public const int TileSize = 16;

        /// <summary>
        /// Per-tile cap: the most fixtures a single tile can light with. Fixtures
        /// past it are dropped for that tile, so a rig dense enough to reach it is
        /// not being drawn as it was authored.
        /// </summary>
        /// <remarks>
        /// This is the only declaration. The cull kernel and the read side take it
        /// as a uniform in <c>w</c> of their tile-params vector rather than
        /// declaring their own, because it decides a buffer stride and three copies
        /// of a stride is a silent-corruption hazard rather than a tidiness one.
        /// </remarks>
        public const int MaxLightsPerTile = 64;

        const int TileStride   = MaxLightsPerTile + 1;   // slot 0 holds the count
        const int CullGroupSize = 64;                    // must match [numthreads]

        static readonly int s_LightsID        = Shader.PropertyToID("_VRSLLights");
        static readonly int s_TileIndicesRWID = Shader.PropertyToID("_VRSLTileLightIndicesRW");
        static readonly int s_LightCountID    = Shader.PropertyToID("_VRSLLightCount");
        static readonly int s_CullTileParamsID = Shader.PropertyToID("_VRSLCullTileParams");
        static readonly int s_InvViewProjID   = Shader.PropertyToID("_VRSLCullInvViewProj0");
        static readonly int s_InvViewProj1ID = Shader.PropertyToID("_VRSLCullInvViewProj1");

        readonly ComputeShader   _cullShader;
        readonly IVRSLLightSource _source;
        readonly int             _kernel;


        GraphicsBuffer _tileBuffer;
        int            _allocatedUints;

        /// <summary>Per-tile light index list. Null until the first successful record.</summary>
        public GraphicsBuffer TileBuffer => _tileBuffer;

        /// <summary>
        /// x = tiles across, y = tiles down, z = tile size in pixels,
        /// w = <see cref="MaxLightsPerTile"/>.
        /// x is 0 when the cull did not run for the current camera, which the
        /// shaders read as "iterate every light" so the scene still lights.
        /// Written during this pass's record and read by the lighting and
        /// volumetric passes, which record later in the same graph.
        /// </summary>
        public Vector4 TileParams { get; private set; }

        /// <summary>Tiles the last record actually dispatched, across all eyes.
        /// The buffer may be larger, since it only ever grows.</summary>
        public int ActiveTileCount { get; private set; }


        /// <summary>Entries per tile in <see cref="TileBuffer"/>: a count followed
        /// by up to <see cref="MaxLightsPerTile"/> indices.</summary>
        public static int Stride => TileStride;

        /// <summary>
        /// What a consuming pass needs to bind for tiling. Derived here rather
        /// than at each of the four call sites (DMX and AudioLink × lighting and
        /// volumetric), because the four have to agree: this decides whether a
        /// shader walks the tile list or falls back to scanning every fixture,
        /// and a copy that drifts would silently change what gets lit.
        /// </summary>
        public readonly struct TileBinding
        {
            /// <summary>Bind <see cref="TileBuffer"/> to <c>_VRSLTileLightIndices</c>.
            /// True whenever the buffer exists, even when tiling is inactive —
            /// leaving a StructuredBuffer slot unbound is not uniformly safe
            /// across graphics APIs.</summary>
            public readonly bool Bind;

            /// <summary>Value for <c>_VRSLTileParams</c>. Zero means "tiling
            /// inactive", which the shaders read as "iterate every light".</summary>
            public readonly Vector4 TileParams;

            public TileBinding(bool bind, Vector4 tileParams)
            {
                Bind       = bind;
                TileParams = tileParams;
            }
        }

        /// <summary>Resolve what this camera's passes should bind. Safe to call on
        /// a null pass via <c>cull?.GetBinding() ?? default</c>.</summary>
        public TileBinding GetBinding()
        {
            if (_tileBuffer == null) return default;
            return new TileBinding(true, TileParams.x >= 1f ? TileParams : Vector4.zero);
        }

        public VRSLTileCullPass(ComputeShader cullShader, IVRSLLightSource source)
        {
            _cullShader = cullShader;
            _source     = source;
            // HasKernel first — FindKernel throws rather than returning -1 when
            // the kernel is missing, which a failed shader compile would cause.
            _kernel     = cullShader != null && cullShader.HasKernel("CullLights")
                              ? cullShader.FindKernel("CullLights")
                              : -1;

            // After the per-fixture decode compute (BeforeRenderingOpaques) and
            // before the lighting pass at AfterRenderingOpaques.
            renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingOpaques + 1);
        }

        class PassData
        {
            public ComputeShader cs;
            public int           kernel;
            public BufferHandle  lightData;
            public BufferHandle  tileIndices;
            public int           lightCount;
            public Vector4       cullTileParams;
            public Matrix4x4[]   invViewProj;
            public int           tilesX;
            public int           tilesY;
            public int           slices;
        }

        public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
        {
            // Zero first: any early-out below has to leave the consumers reading
            // "tiling inactive" rather than a stale grid from another camera.
            TileParams = Vector4.zero;
            ActiveTileCount = 0;

            // Keep a buffer allocated even when the cull can't run, so the
            // consuming passes always have something to bind to
            // _VRSLTileLightIndices. The shaders never read it while
            // TileParams.x is zero, but leaving a StructuredBuffer slot unbound
            // is the kind of thing that behaves differently per graphics API.
            EnsureBuffer(TileStride);

            if (_cullShader == null || _kernel < 0) return;
            if (_source == null || _source.FixtureCount == 0
                || _source.LightDataBuffer == null) return;

            var camData   = frame.Get<UniversalCameraData>();
            var resources = frame.Get<UniversalResourceData>();

            var desc   = camData.cameraTargetDescriptor;
            int slices = Mathf.Max(1, desc.volumeDepth);
            int tilesX = Mathf.Max(1, Mathf.CeilToInt(desc.width  / (float)TileSize));
            int tilesY = Mathf.Max(1, Mathf.CeilToInt(desc.height / (float)TileSize));

            if (!EnsureBuffer(tilesX * tilesY * slices * TileStride)) return;

            // Reproduce the exact reconstruction the fullscreen shaders use:
            // UNITY_MATRIX_I_VP is inverse(view) * inverse(gpuProjection), and
            // whether the GPU projection carries a Y flip depends on rendering
            // into a texture rather than straight to the backbuffer. Getting
            // this wrong would flip the tile grid against the sampling shaders,
            // so it is derived rather than assumed.
            bool renderIntoTexture = !resources.isActiveTargetBackBuffer;

            // Clip-Y flip folded in here rather than applied in the compute. The
            // shader-side UNITY_UV_STARTS_AT_TOP macro isn't defined for compute
            // shaders, so relying on it there mirrored the reconstruction against
            // the fragment shaders that consume it.
            var clipFlip = Matrix4x4.identity;
            if (SystemInfo.graphicsUVStartsAtTop) clipFlip.m11 = -1f;
            // Allocated per record rather than reused. SetComputeMatrixArrayParam
            // reads this when the graph executes, so a field shared across records
            // would let one camera's dispatch pick up another camera's matrices if
            // recording and execution ever interleave. 128 bytes a frame is not
            // worth the coupling.
            var invViewProj = new Matrix4x4[2];
            for (int view = 0; view < 2; view++)
            {
                int src = Mathf.Min(view, slices - 1);
                Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(
                    camData.GetProjectionMatrix(src), renderIntoTexture);
                invViewProj[view] =
                    Matrix4x4.Inverse(camData.GetViewMatrix(src))
                    * Matrix4x4.Inverse(gpuProj) * clipFlip;
            }

            TileParams      = new Vector4(tilesX, tilesY, TileSize, MaxLightsPerTile);
            ActiveTileCount = tilesX * tilesY * slices;

            using var builder = rg.AddComputePass<PassData>("VRSL Tile Light Cull", out var d);

            // The tile list is consumed through SetGlobalBuffer in later passes
            // rather than a tracked read on this handle, so the graph would see
            // the write as a dead store and cull the pass.
            builder.AllowPassCulling(false);

            d.cs             = _cullShader;
            d.kernel         = _kernel;
            d.lightData      = rg.ImportBuffer(_source.LightDataBuffer);
            d.tileIndices    = rg.ImportBuffer(_tileBuffer);
            d.lightCount     = _source.FixtureCount;
            d.cullTileParams = new Vector4(tilesX, tilesY, slices, MaxLightsPerTile);
            d.invViewProj    = invViewProj;
            d.tilesX         = tilesX;
            d.tilesY         = tilesY;
            d.slices         = slices;

            builder.UseBuffer(d.lightData,   AccessFlags.Read);
            builder.UseBuffer(d.tileIndices, AccessFlags.Write);

            builder.SetRenderFunc((PassData p, ComputeGraphContext ctx) =>
            {
                var cmd = ctx.cmd;
                cmd.SetComputeIntParam(        p.cs,           s_LightCountID,     p.lightCount);
                cmd.SetComputeVectorParam(     p.cs,           s_CullTileParamsID, p.cullTileParams);
                cmd.SetComputeMatrixParam(     p.cs,           s_InvViewProjID,    p.invViewProj[0]);
                cmd.SetComputeMatrixParam(     p.cs,           s_InvViewProj1ID,   p.invViewProj[1]);
                cmd.SetComputeBufferParam(     p.cs, p.kernel, s_LightsID,         p.lightData);
                cmd.SetComputeBufferParam(     p.cs, p.kernel, s_TileIndicesRWID,  p.tileIndices);

                // One group per tile per eye; the group's 64 threads stride the
                // light list between them.
                cmd.DispatchCompute(p.cs, p.kernel, p.tilesX, p.tilesY, p.slices);
            });
        }

        // Grow-only: a scene with several cameras at different resolutions would
        // otherwise reallocate every frame. Tiles past the current grid are never
        // read, so an oversized buffer is harmless.
        bool EnsureBuffer(int requiredUints)
        {
            if (requiredUints <= 0) return false;
            if (_tileBuffer != null && _allocatedUints >= requiredUints) return true;

            _tileBuffer?.Release();
            _tileBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, requiredUints, sizeof(uint));
            _allocatedUints = requiredUints;
            return true;
        }

        public void Dispose()
        {
            _tileBuffer?.Release();
            _tileBuffer     = null;
            _allocatedUints = 0;
        }
    }
}
