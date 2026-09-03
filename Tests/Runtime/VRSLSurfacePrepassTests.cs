// VRSLImageCompare is editor-only, and this assembly builds for players too, so
// these rows compile only where their helper exists.
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The surface prepass rows, S10 to S14: what it draws, for whom, where its
    /// normals may come from instead, and whether what it captured reaches the
    /// lighting pass.
    ///
    /// S13 asks what URP's normals texture holds beside VRSL's own, per renderer
    /// configuration. VRSL draws its own normals prepass with the same shader tags
    /// URP's uses, so the two textures should hold the same data wherever URP's can
    /// be produced at all. Whether it can is the question: under depth priming
    /// URP's depth-normals prepass draws into the camera's depth attachment, and a
    /// URP that primes with MSAA on puts a multisampled depth beside a normals
    /// target that is never multisampled. The row asks each configuration directly
    /// rather than reasoning about it, because the answer decides whether the
    /// lighting pass may read URP's texture instead of drawing its own.
    /// </summary>
    class VRSLSurfacePrepassTests : VRSLDMXTest
    {
        const int ImageSize    = 512;
        const int WarmUpFrames = 60;

        /// <summary>A channel step either side. Both textures are 8-bit signed and
        /// read back through the same remap, so an honest match is exact and one
        /// step is representation.</summary>
        const int Tolerance = 2;

        /// <summary>
        /// Asks URP for normals and, once opaques are drawn, copies both normals
        /// textures out to targets the test owns. The copy is a fullscreen draw
        /// through the probe shader: pass 0 reads <c>_VRSLNormalsTexture</c>, pass 5
        /// reads <c>_CameraNormalsTexture</c>. With <see cref="Extras"/> set, passes 1
        /// to 4 copy the albedo, material, camera depth and surface depth out as well.
        /// </summary>
        sealed class ProbePass : ScriptableRenderPass
        {
            class Data
            {
                public TextureHandle   vrsl, urp;
                public TextureHandle[] extras;
                public Material        material;
            }

            readonly Material _material;
            readonly RTHandle _vrsl, _urp;
            public RTHandle[] Extras;

            public int  ObservedMsaa   { get; private set; }
            public bool UrpHandleValid { get; private set; }
            public int  Recorded       { get; private set; }

            public ProbePass(Material material, RTHandle vrsl, RTHandle urp, bool requestNormals = true)
            {
                _material = material;
                _vrsl     = vrsl;
                _urp      = urp;
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
                // Normal is what turns URP's prepass from depth-only into
                // depth-and-normals. Depth is what the package's passes ask for.
                ConfigureInput(requestNormals
                    ? ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal
                    : ScriptableRenderPassInput.Depth);
            }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                var camData   = frame.Get<UniversalCameraData>();
                var resources = frame.Get<UniversalResourceData>();
                ObservedMsaa   = camData.cameraTargetDescriptor.msaaSamples;
                UrpHandleValid = resources.cameraNormalsTexture.IsValid();
                Recorded++;

                using var builder = rg.AddUnsafePass<Data>("VRSL Normals Probe", out var d);
                d.material = _material;
                d.vrsl     = rg.ImportTexture(_vrsl);
                d.urp      = rg.ImportTexture(_urp);
                builder.UseTexture(d.vrsl, AccessFlags.Write);
                builder.UseTexture(d.urp,  AccessFlags.Write);
                if (Extras != null)
                {
                    d.extras = new TextureHandle[Extras.Length];
                    for (int i = 0; i < Extras.Length; i++)
                    {
                        d.extras[i] = rg.ImportTexture(Extras[i]);
                        builder.UseTexture(d.extras[i], AccessFlags.Write);
                    }
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                }
                if (UrpHandleValid)
                    builder.UseTexture(resources.cameraNormalsTexture, AccessFlags.Read);
                // Both sources are globals set after the passes that write them;
                // declaring every global read is what orders this pass after both.
                builder.UseAllGlobalTextures(true);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((Data p, UnsafeGraphContext ctx) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    cmd.SetRenderTarget(p.vrsl);
                    cmd.DrawProcedural(Matrix4x4.identity, p.material, 0, MeshTopology.Triangles, 3);
                    cmd.SetRenderTarget(p.urp);
                    cmd.DrawProcedural(Matrix4x4.identity, p.material, 5, MeshTopology.Triangles, 3);
                    if (p.extras != null)
                        for (int i = 0; i < p.extras.Length; i++)
                        {
                            cmd.SetRenderTarget(p.extras[i]);
                            cmd.DrawProcedural(Matrix4x4.identity, p.material, 1 + i, MeshTopology.Triangles, 3);
                        }
                });
            }
        }

        sealed class Outcome
        {
            public string           name;
            public DepthPrimingMode priming;
            public int              msaaAsked, msaaSeen, recorded;
            public int              targetSamples, assetMsaa, supportedSamples;
            public bool             allowMsaa, storeAndResolve;
            public bool             urpHandle;
            public int              vrslWritten, urpWritten, both, onlyVrsl, onlyUrp, differing, maxDiff;
            public Vector3          vrslMean, urpMean;
            public float            frameLitPercent;
            public readonly List<string> errors = new();

            public override string ToString()
                => $"{name}: msaa {msaaSeen} (asked {msaaAsked}; target {targetSamples}, asset {assetMsaa}, "
                 + $"allowMSAA {allowMsaa}, platform allows {supportedSamples}, store+resolve {storeAndResolve}), "
                 + $"recorded {recorded}, "
                 + $"URP handle {(urpHandle ? "valid" : "invalid")}, "
                 + $"VRSL wrote {vrslWritten}, URP wrote {urpWritten}, both {both}, "
                 + $"only VRSL {onlyVrsl}, only URP {onlyUrp}, differing {differing} (max {maxDiff}/255), "
                 + $"mean VRSL {vrslMean:F3} URP {urpMean:F3}, frame lit {frameLitPercent:F2}%, "
                 + $"errors {errors.Count}"
                 + (errors.Count > 0 ? $" — first: {errors[0]}" : "");
        }

        /// <summary>
        /// Put every renderer's priming where the row wants it. Called before each
        /// render as well as up front: a host quality module rewrites priming at a
        /// moment of its own, and one of these rows found its second capture running
        /// with priming Disabled after setting Forced thirty seconds earlier.
        /// </summary>
        static void HoldPriming(List<UniversalRendererData> renderers, DepthPrimingMode mode)
        {
            foreach (var r in renderers)
                if (r != null && r.depthPrimingMode != mode) r.depthPrimingMode = mode;
        }

        static List<UniversalRendererData> Renderers()
        {
            var found = new List<UniversalRendererData>();
            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset urp)
                return found;
            foreach (var data in urp.rendererDataList)
                if (data is UniversalRendererData universal) found.Add(universal);
            return found;
        }

        static RenderTexture ProbeTarget(string name)
        {
            var rt = new RenderTexture(ImageSize, ImageSize, 0, GraphicsFormat.R8G8B8A8_UNorm) { name = name };
            rt.Create();
            // Cleared up front so a frame the graph refused to render leaves an
            // empty texture rather than whatever the pool held.
            var cmd = new CommandBuffer();
            cmd.SetRenderTarget(rt);
            cmd.ClearRenderTarget(true, true, Color.clear);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            return rt;
        }

        /// <summary>
        /// Render the rig at the given priming and MSAA with the probe attached, and
        /// tally what the two normals textures hold. Yields <c>null</c> itself: the
        /// runner drives one level of nesting.
        /// </summary>
        static IEnumerator Probe(DepthPrimingMode mode, int msaa, System.Action<Outcome> done)
        {
            var outcome = new Outcome { name = $"priming {mode}, MSAA {msaa}x", priming = mode, msaaAsked = msaa };

            var renderers = Renderers();
            var restore = new Dictionary<UniversalRendererData, DepthPrimingMode>();
            foreach (var r in renderers) restore[r] = r.depthPrimingMode;
            HoldPriming(renderers, mode);

            void OnLog(string condition, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception) outcome.errors.Add(condition);
            }
            Application.logMessageReceived += OnLog;

            Material material = null;
            RenderTexture vrslRt = null, urpRt = null;
            RTHandle vrslHandle = null, urpHandle = null;
            Texture2D vrslTex = null, urpTex = null, frame = null;
            System.Action<ScriptableRenderContext, Camera> enqueue = null;
            try
            {
                var shader = Shader.Find("Hidden/VRSL-URP/Tests/NormalsProbe");
                Assert.IsNotNull(shader, "the probe shader did not compile or is not in the project");
                material   = new Material(shader);
                vrslRt     = ProbeTarget("VRSL normals probe (VRSL)");
                urpRt      = ProbeTarget("VRSL normals probe (URP)");
                vrslHandle = RTHandles.Alloc(vrslRt);
                urpHandle  = RTHandles.Alloc(urpRt);
                var probe  = new ProbePass(material, vrslHandle, urpHandle);

                using var rig = VRSLDMXRig.Build(targetSize: ImageSize, msaa: msaa);
                rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
                rig.Source.speed   = 0f;
                rig.Manager.enabled = false;
                rig.Manager.enabled = true;
                rig.FreezeForImageCapture();

                var target = rig.Camera;
                enqueue = (ctx, cam) =>
                {
                    if (cam != target) return;
                    var renderer = cam.GetUniversalAdditionalCameraData()?.scriptableRenderer;
                    renderer?.EnqueuePass(probe);
                };
                RenderPipelineManager.beginCameraRendering += enqueue;

                for (int i = 0; i < WarmUpFrames; i++)
                {
                    yield return null;
                    HoldPriming(renderers, mode);
                    rig.RenderFrame();
                }

                RenderPipelineManager.beginCameraRendering -= enqueue;
                enqueue = null;

                outcome.msaaSeen  = probe.ObservedMsaa;
                outcome.urpHandle = probe.UrpHandleValid;
                outcome.recorded  = probe.Recorded;
                // Every input to URP's sample-count decision, so a configuration that
                // was not reached says which one refused it rather than leaving it
                // to be guessed from source.
                outcome.targetSamples    = rig.Target.antiAliasing;
                outcome.allowMsaa        = rig.Camera.allowMSAA;
                outcome.assetMsaa        = GraphicsSettings.currentRenderPipeline
                                               is UniversalRenderPipelineAsset pipeline ? pipeline.msaaSampleCount : 0;
                outcome.supportedSamples = SystemInfo.GetRenderTextureSupportedMSAASampleCount(rig.Target.descriptor);
                outcome.storeAndResolve  = SystemInfo.supportsStoreAndResolveAction;

                vrslTex = VRSLImageCompare.Read(vrslRt);
                urpTex  = VRSLImageCompare.Read(urpRt);
                frame   = VRSLImageCompare.Read(rig.Target);
                Tally(outcome, vrslTex.GetPixels32(), urpTex.GetPixels32(), frame.GetPixels32());
            }
            finally
            {
                if (enqueue != null) RenderPipelineManager.beginCameraRendering -= enqueue;
                Application.logMessageReceived -= OnLog;
                foreach (var pair in restore)
                    if (pair.Key != null) pair.Key.depthPrimingMode = pair.Value;
                if (vrslTex != null) Object.DestroyImmediate(vrslTex);
                if (urpTex  != null) Object.DestroyImmediate(urpTex);
                if (frame   != null) Object.DestroyImmediate(frame);
                vrslHandle?.Release();
                urpHandle?.Release();
                if (vrslRt != null) { vrslRt.Release(); Object.DestroyImmediate(vrslRt); }
                if (urpRt  != null) { urpRt.Release();  Object.DestroyImmediate(urpRt); }
                if (material != null) Object.DestroyImmediate(material);
            }

            done(outcome);
        }

        static void Tally(Outcome o, Color32[] vrsl, Color32[] urp, Color32[] frame)
        {
            Vector3 vrslSum = Vector3.zero, urpSum = Vector3.zero;
            for (int i = 0; i < vrsl.Length; i++)
            {
                var a = vrsl[i];
                var b = urp[i];
                bool wroteA = a.a > 127, wroteB = b.a > 127;
                if (wroteA) { o.vrslWritten++; vrslSum += Decode(a); }
                if (wroteB) { o.urpWritten++;  urpSum  += Decode(b); }
                if (wroteA && wroteB)
                {
                    o.both++;
                    int d = Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b));
                    if (d > Tolerance) o.differing++;
                    if (d > o.maxDiff) o.maxDiff = d;
                }
                else if (wroteA) o.onlyVrsl++;
                else if (wroteB) o.onlyUrp++;
            }
            if (o.vrslWritten > 0) o.vrslMean = vrslSum / o.vrslWritten;
            if (o.urpWritten  > 0) o.urpMean  = urpSum  / o.urpWritten;

            int lit = 0;
            foreach (var p in frame)
                if (p.r > 8 || p.g > 8 || p.b > 8) lit++;
            o.frameLitPercent = 100f * lit / frame.Length;
        }

        static Vector3 Decode(Color32 c)
            => new Vector3(c.r / 255f * 2f - 1f, c.g / 255f * 2f - 1f, c.b / 255f * 2f - 1f);

        /// <summary>
        /// Render the rig with the floor on <paramref name="floorLayer"/> and the DMX
        /// manager's prepass mask set as given, and hand back the frame. Yields
        /// <c>null</c> itself: the runner drives one level of nesting.
        /// </summary>
        static IEnumerator CaptureWithMask(int floorLayer, LayerMask mask,
                                           System.Action<Texture2D> onCaptured)
        {
            using var rig = VRSLDMXRig.Build(targetSize: ImageSize);
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
            rig.Source.speed   = 0f;
            rig.Manager.enabled = false;
            rig.Manager.enabled = true;
            rig.FreezeForImageCapture();
            rig.Floor.layer = floorLayer;
            // Read by the manager before each enqueue, so no bounce is needed.
            rig.Manager.prepassLayers = mask;

            for (int i = 0; i < WarmUpFrames; i++)
            {
                yield return null;
                rig.RenderFrame();
            }
            onCaptured(VRSLImageCompare.Read(rig.Target));
        }

        /// <summary>Two layers no renderer in the scene is on, so moving the floor
        /// to one and excluding the other is the only thing that changes.</summary>
        static bool FindSpareLayers(out int first, out int second)
        {
            var occupied = new HashSet<int>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (r != null) occupied.Add(r.gameObject.layer);
            first = second = -1;
            for (int layer = 31; layer >= 8; layer--)
            {
                if (occupied.Contains(layer)) continue;
                if (first < 0) first = layer;
                else { second = layer; return true; }
            }
            return false;
        }

        static float LitPercent(Texture2D frame)
        {
            var pixels = frame.GetPixels32();
            int lit = 0;
            foreach (var p in pixels)
                if (p.r > 8 || p.g > 8 || p.b > 8) lit++;
            return 100f * lit / pixels.Length;
        }

        /// <summary>Mean colour of the lit pixels, in 0..255.</summary>
        static Vector3 LitMean(Texture2D frame)
        {
            var pixels = frame.GetPixels32();
            Vector3 sum = Vector3.zero;
            int lit = 0;
            foreach (var p in pixels)
            {
                if (p.r <= 8 && p.g <= 8 && p.b <= 8) continue;
                sum += new Vector3(p.r, p.g, p.b);
                lit++;
            }
            return lit > 0 ? sum / lit : Vector3.zero;
        }

        sealed class SourceReport
        {
            public bool   usesUrp;
            public string reason;
            public int    ownDraws, urpReads;
        }

        /// <summary>
        /// Render the rig at the given sample count with the normals source forced
        /// or left to the policy, and hand back the frame with what the prepass
        /// did. Yields <c>null</c> itself: the runner drives one level of nesting.
        /// </summary>
        static IEnumerator CaptureNormalsSource(bool forceOwn, int msaa, DepthPrimingMode priming,
                                                System.Action<Texture2D> onCaptured,
                                                System.Action<SourceReport> onReport)
        {
            var renderers = Renderers();
            HoldPriming(renderers, priming);
            using var rig = VRSLDMXRig.Build(targetSize: ImageSize, msaa: msaa);
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
            rig.Source.speed   = 0f;
            rig.Manager.enabled = false;
            rig.Manager.enabled = true;
            rig.FreezeForImageCapture();
            // Read per camera, so no bounce; and after the bounce, since the prepass
            // whose counters are read is the one the bounce built.
            rig.Manager.forceOwnNormals = forceOwn;

            for (int i = 0; i < WarmUpFrames; i++)
            {
                yield return null;
                HoldPriming(renderers, priming);
                rig.RenderFrame();
            }

            onReport(new SourceReport
            {
                usesUrp  = rig.Manager.UsesUrpNormals,
                reason   = rig.Manager.NormalsSource,
                ownDraws = rig.Manager.SurfacePrepass.OwnNormalsDraws,
                urpReads = rig.Manager.SurfacePrepass.UrpNormalsReads,
            });
            onCaptured(VRSLImageCompare.Read(rig.Target));
        }

        /// <summary>
        /// S11. Where URP's normals can be read, VRSL's own normals draw does not run,
        /// and the frame is the one the draw would have produced.
        ///
        /// Priming Forced at MSAA 1, which is where the reuse engages and the
        /// configuration the host ships. Two captures, the policy left to decide and
        /// then overridden with Force own normals: the first must have read URP's
        /// texture on every render and drawn nothing of its own, the second the
        /// reverse, and the two frames must agree. The counters are the automated
        /// half of "one opaque geometry pass fewer"; the pass list itself is read in
        /// a frame capture by hand.
        /// </summary>
        [UnityTest]
        public IEnumerator S11_ReuseSkipsTheNormalsDrawAndKeepsTheFrame()
        {
            var renderers = Renderers();
            if (renderers.Count == 0)
                Assert.Ignore("No Universal Renderer on the active pipeline asset.");
            var restore = new Dictionary<UniversalRendererData, DepthPrimingMode>();
            foreach (var r in renderers) restore[r] = r.depthPrimingMode;

            Texture2D reused = null, own = null;
            SourceReport policy = null, forced = null;
            try
            {
                yield return CaptureNormalsSource(false, 1, DepthPrimingMode.Forced, t => reused = t, r => policy = r);
                yield return CaptureNormalsSource(true,  1, DepthPrimingMode.Forced, t => own    = t, r => forced = r);

                var result = VRSLImageCompare.Compare(reused, own);
                Debug.Log($"[S11] policy: {policy.reason} (own draws {policy.ownDraws}, URP reads "
                        + $"{policy.urpReads}); forced: {forced.reason} (own draws {forced.ownDraws}, "
                        + $"URP reads {forced.urpReads}); frames differ by {result}");

                if (!policy.usesUrp)
                    Assert.Inconclusive("the policy did not engage the reuse on this renderer, so "
                                      + $"the row has nothing to compare: {policy.reason}");

                Assert.AreEqual(0, policy.ownDraws,
                    "VRSL drew its own normals while reading URP's, so the saving did not happen");
                Assert.Greater(policy.urpReads, 0,
                    "the prepass never published URP's texture, so the lighting pass read nothing");
                Assert.AreEqual(0, forced.urpReads,
                    "Force own normals is on and the prepass still read URP's texture");
                Assert.Greater(forced.ownDraws, 0,
                    "Force own normals is on and the prepass drew nothing");

                Assert.LessOrEqual(result.DifferingPercent, 0.05f,
                    $"reading URP's normals changed {result.DifferingPercent:F3}% of the frame "
                  + "against drawing VRSL's own. S13 shows the two textures identical, so the "
                  + "lighting pass is reading something other than the texture it was given");
            }
            finally
            {
                foreach (var pair in restore)
                    if (pair.Key != null) pair.Key.depthPrimingMode = pair.Value;
                if (reused != null) Object.DestroyImmediate(reused);
                if (own    != null) Object.DestroyImmediate(own);
            }
        }

        /// <summary>
        /// S10. The same scene at MSAA 1 and MSAA 4 shades its surfaces the same,
        /// whichever source the normals came from.
        ///
        /// Priming Disabled, where URP's normals are read at both sample counts, so
        /// the MSAA comparison varies only MSAA: multisampling moves every geometry
        /// edge, so the frames are not compared pixel for pixel, and the lit area
        /// and the mean colour of the lit pixels are what shading is judged on. A
        /// third capture at 4x with Force own normals varies only the source, and
        /// that pair is held to the pixel. Under priming Forced the 4x frame is
        /// dominated by S14 instead, which is why this row does not run there.
        /// </summary>
        [UnityTest]
        public IEnumerator S10_SurfaceShadingMatchesAcrossMsaa()
        {
            var renderers = Renderers();
            if (renderers.Count == 0)
                Assert.Ignore("No Universal Renderer on the active pipeline asset.");
            var restore = new Dictionary<UniversalRendererData, DepthPrimingMode>();
            foreach (var r in renderers) restore[r] = r.depthPrimingMode;

            Texture2D at1 = null, at4 = null, at4Own = null;
            SourceReport source1 = null, source4 = null, source4Own = null;
            try
            {
                yield return CaptureNormalsSource(false, 1, DepthPrimingMode.Disabled, t => at1    = t, r => source1    = r);
                yield return CaptureNormalsSource(false, 4, DepthPrimingMode.Disabled, t => at4    = t, r => source4    = r);
                yield return CaptureNormalsSource(true,  4, DepthPrimingMode.Disabled, t => at4Own = t, r => source4Own = r);

                float lit1 = LitPercent(at1), lit4 = LitPercent(at4);
                var mean1 = LitMean(at1);
                var mean4 = LitMean(at4);
                var msaaPair   = VRSLImageCompare.Compare(at1, at4);
                var sourcePair = VRSLImageCompare.Compare(at4, at4Own);
                Debug.Log($"[S10] 1x: {source1.reason}; 4x: {source4.reason}; 4x forced: {source4Own.reason}; "
                        + $"lit {lit1:F2}% against {lit4:F2}%, lit mean {mean1:F2} against {mean4:F2}, "
                        + $"1x vs 4x {msaaPair}; 4x URP vs 4x own {sourcePair}");

                if (!source1.usesUrp || !source4.usesUrp)
                    Assert.Inconclusive("the policy did not read URP's normals at both sample counts, so "
                                      + $"this row is not the comparison it claims: 1x {source1.reason}; 4x {source4.reason}");
                if (source4Own.usesUrp)
                    Assert.Inconclusive("Force own normals did not take, so the source was not varied at 4x");

                Assert.Greater(lit1, 5f, "the 1x frame is nearly dark, so the row has nothing to judge");
                Assert.AreEqual(lit1, lit4, 0.5f,
                    $"the lit area moved from {lit1:F2}% to {lit4:F2}% between MSAA 1 and 4, which "
                  + "is more than edges account for");
                for (int c = 0; c < 3; c++)
                    Assert.AreEqual(mean1[c], mean4[c], 2f,
                        $"the mean lit colour moved by more than 2 of 255 on channel {c} between "
                      + "MSAA 1 and 4, so surfaces are shading differently with the sample count");
                Assert.LessOrEqual(sourcePair.DifferingPercent, 0.05f,
                    $"at MSAA 4 reading URP's normals changed {sourcePair.DifferingPercent:F3}% of the "
                  + "frame against drawing VRSL's own");
            }
            finally
            {
                foreach (var pair in restore)
                    if (pair.Key != null) pair.Key.depthPrimingMode = pair.Value;
                if (at1    != null) Object.DestroyImmediate(at1);
                if (at4    != null) Object.DestroyImmediate(at4);
                if (at4Own != null) Object.DestroyImmediate(at4Own);
            }
        }

        /// <summary>
        /// Render the rig with volumetrics off and, optionally, the floor left out of
        /// the prepass, so the frame is surface lighting alone. Yields <c>null</c>
        /// itself: the runner drives one level of nesting.
        /// </summary>
        static IEnumerator CaptureSurfaceOnly(int msaa, DepthPrimingMode priming, bool excludeFloor,
                                              System.Action<Texture2D> onCaptured)
        {
            var renderers = Renderers();
            HoldPriming(renderers, priming);
            using var rig = VRSLDMXRig.Build(targetSize: ImageSize, msaa: msaa);
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
            rig.Source.speed   = 0f;
            rig.Manager.quality = VRSLQuality.Off;
            rig.Manager.enabled = false;
            rig.Manager.enabled = true;
            rig.FreezeForImageCapture();
            if (excludeFloor && FindSpareLayers(out int spare, out _))
            {
                rig.Floor.layer = spare;
                rig.Manager.prepassLayers = ~0 & ~(1 << spare);
            }

            for (int i = 0; i < WarmUpFrames; i++)
            {
                yield return null;
                HoldPriming(renderers, priming);
                rig.RenderFrame();
            }
            onCaptured(VRSLImageCompare.Read(rig.Target));
        }

        /// <summary>
        /// S14. A surface keeps its captured colour and gloss under depth priming
        /// with MSAA on.
        ///
        /// Volumetrics off, priming Forced, the floor lit at MSAA 1 and at MSAA 2
        /// (the sample count Basis ships), against the same frame at 2x with the
        /// floor left out of the prepass so it lights from the neutral fallback. The
        /// floor at 2x has to shade as it does at 1x, not as the fallback.
        ///
        /// What this row found on 2026-09-03, and what it fails on today: under
        /// priming with MSAA, <c>_CameraDepthTexture</c> is a resolve of the
        /// multisampled depth attachment, which takes the farthest sample, and on a
        /// surface seen at a grazing angle that sits several centimetres from the
        /// depth the prepass rasterised at the pixel centre. The surface-data gate
        /// (<c>VRSL_SurfaceDataCovers</c>) allows 0.05% of eye depth, a few
        /// millimetres at these distances, so it rejects the whole floor and the
        /// lighting pass falls back to mid-grey. Widening the tolerance to 2% for
        /// one run made 1x and 2x agree to 0.25% of pixels, which is how the
        /// mechanism was confirmed. Without priming the depth prepass writes the
        /// camera depth texture directly at one sample and the gate holds.
        /// </summary>
        [UnityTest]
        public IEnumerator S14_SurfacesKeepTheirDataUnderPrimingWithMsaa()
        {
            var renderers = Renderers();
            if (renderers.Count == 0)
                Assert.Ignore("No Universal Renderer on the active pipeline asset.");
            var restore = new Dictionary<UniversalRendererData, DepthPrimingMode>();
            foreach (var r in renderers) restore[r] = r.depthPrimingMode;

            Texture2D at1 = null, at2 = null, at2Fallback = null;
            try
            {
                yield return CaptureSurfaceOnly(1, DepthPrimingMode.Forced, false, t => at1 = t);
                yield return CaptureSurfaceOnly(2, DepthPrimingMode.Forced, false, t => at2 = t);
                yield return CaptureSurfaceOnly(2, DepthPrimingMode.Forced, true,  t => at2Fallback = t);

                var mean1  = LitMean(at1);
                var mean2  = LitMean(at2);
                var meanFb = LitMean(at2Fallback);
                Debug.Log($"[S14] lit mean at 1x {mean1:F2}, at 2x {mean2:F2}, at 2x with the floor on the "
                        + $"fallback {meanFb:F2}; 1x vs 2x {VRSLImageCompare.Compare(at1, at2)}");

                Assert.Greater(LitPercent(at1), 5f, "the 1x frame is nearly dark, so the row has nothing to judge");

                // The floor's data has to be making a difference at 1x for the row to
                // have anything to protect at 2x.
                float apart = 0f;
                for (int c = 0; c < 3; c++) apart = Mathf.Max(apart, Mathf.Abs(mean1[c] - meanFb[c]));
                if (apart <= 2f)
                    Assert.Inconclusive("the floor lights the same with and without its captured data, "
                                      + "so this row cannot tell whether that data reached the lighting pass");

                for (int c = 0; c < 3; c++)
                    Assert.AreEqual(mean1[c], mean2[c], 2f,
                        $"channel {c}: the lit mean is {mean2[c]:F1} at MSAA 2x against {mean1[c]:F1} at 1x, "
                      + $"and {meanFb[c]:F1} with the floor on the neutral fallback. Under priming with "
                      + "MSAA the surface-data gate is rejecting the resolved camera depth and every "
                      + "surface lights as mid-grey. See VRSL_SurfaceDataCovers and "
                      + "VRSL_SURFACE_DEPTH_TOLERANCE.");
            }
            finally
            {
                foreach (var pair in restore)
                    if (pair.Key != null) pair.Key.depthPrimingMode = pair.Value;
                if (at1         != null) Object.DestroyImmediate(at1);
                if (at2         != null) Object.DestroyImmediate(at2);
                if (at2Fallback != null) Object.DestroyImmediate(at2Fallback);
            }
        }

        /// <summary>
        /// S12. Geometry on a layer the prepass mask leaves out lights as neutral grey
        /// rather than black, and geometry inside the mask is unaffected.
        ///
        /// Three frames of one scene: the mask at everything, the mask leaving out the
        /// floor's layer, and the mask leaving out a layer nothing is on. The third is
        /// what makes the second mean anything. A mask that excludes an empty layer
        /// must render the same frame as no mask at all, which is the row's proof that
        /// geometry inside the mask was untouched; a mask that excludes the floor must
        /// change the frame, which is the proof it applied, and must leave the floor
        /// lit, which is the difference between grey and black.
        /// </summary>
        [UnityTest]
        public IEnumerator S12_ALayerLeftOutOfThePrepassLightsGreyNotBlack()
        {
            if (!FindSpareLayers(out int floorLayer, out int emptyLayer))
                Assert.Ignore("fewer than two layers are free of renderers in this scene, so "
                            + "the row cannot move the floor to one and exclude another.");

            Texture2D everything = null, withoutFloor = null, withoutEmpty = null;
            try
            {
                yield return CaptureWithMask(floorLayer, ~0, t => everything = t);
                yield return CaptureWithMask(floorLayer, ~0 & ~(1 << floorLayer), t => withoutFloor = t);
                yield return CaptureWithMask(floorLayer, ~0 & ~(1 << emptyLayer), t => withoutEmpty = t);

                float litAll     = LitPercent(everything);
                float litNoFloor = LitPercent(withoutFloor);
                var control = VRSLImageCompare.Compare(everything, withoutEmpty);
                var effect  = VRSLImageCompare.Compare(everything, withoutFloor);
                Debug.Log($"[S12] floor on layer {floorLayer}, empty layer {emptyLayer}: "
                        + $"lit {litAll:F2}% against {litNoFloor:F2}% with the floor left out; "
                        + $"excluding the empty layer moved {control}; excluding the floor moved {effect}");

                Assert.Greater(litAll, 5f, "the frame with everything in the prepass is nearly "
                                         + "dark, so the row has nothing to judge");

                // Inside the mask, unaffected: a mask that leaves out a layer nothing
                // is on has to render the same frame as no mask at all.
                Assert.LessOrEqual(control.DifferingPercent, 0.05f,
                    $"leaving an empty layer out of the prepass moved {control.DifferingPercent:F3}% "
                  + "of the frame, so the mask is touching geometry inside it");

                // Applied: the floor's own colour and gloss are gone from the frame.
                Assert.Greater(effect.DifferingPercent, 1f,
                    $"leaving the floor's layer out of the prepass moved {effect.DifferingPercent:F3}% "
                  + "of the frame, so the mask did not apply to either draw");

                // Grey, not black: the floor is still lit. The neutral fallback is a
                // mid-grey dielectric against a white URP Lit plane, so the frame dims
                // and does not go out.
                Assert.GreaterOrEqual(litNoFloor, 0.9f * litAll,
                    $"the lit area fell from {litAll:F2}% to {litNoFloor:F2}% with the floor left "
                  + "out of the prepass. A surface outside the mask must still light, as neutral "
                  + "grey; going dark means the lighting pass is rejecting it rather than falling "
                  + "back");
            }
            finally
            {
                if (everything   != null) Object.DestroyImmediate(everything);
                if (withoutFloor != null) Object.DestroyImmediate(withoutFloor);
                if (withoutEmpty != null) Object.DestroyImmediate(withoutEmpty);
            }
        }

        /// <summary>
        /// S13. URP's normals texture matches VRSL's own wherever URP can produce it,
        /// and the configuration where it cannot is named.
        ///
        /// Four configurations: priming Forced and Disabled, each at MSAA 1 and 2.
        /// The frame is the rig's, whose camera renders into a texture, so MSAA is
        /// set on that texture: a camera with a target takes its sample count from
        /// the target, not from the pipeline asset, and the asset only has to allow
        /// multisampling at all.
        ///
        /// The expectation the row holds: wherever URP's prepass can draw at all, the
        /// two textures are the same data, world space. That is MSAA 1 under either
        /// priming mode, and MSAA above 1 with priming off, where the prepass draws
        /// into the single-sample camera depth texture. Under priming with MSAA it
        /// draws into the multisampled depth attachment beside a normals target that
        /// is never multisampled, and a project gets a Render Graph error and no
        /// frame at all. That outcome is what the reuse policy has to refuse.
        /// </summary>
        [UnityTest]
        public IEnumerator S13_UrpNormalsMatchVrslNormalsWhereUrpCanProduceThem()
        {
            if (Renderers().Count == 0)
                Assert.Ignore("No Universal Renderer on the active pipeline asset.");

            int assetMsaa = GraphicsSettings.currentRenderPipeline
                                is UniversalRenderPipelineAsset a ? a.msaaSampleCount : 0;

            var outcomes = new List<Outcome>();
            bool ignoreWas = LogAssert.ignoreFailingMessages;
            // The MSAA-under-priming configuration is expected to log a Render Graph
            // error, and that error is this row's evidence rather than its failure.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                yield return Probe(DepthPrimingMode.Forced,   1, outcomes.Add);
                yield return Probe(DepthPrimingMode.Disabled, 1, outcomes.Add);
                yield return Probe(DepthPrimingMode.Forced,   2, outcomes.Add);
                yield return Probe(DepthPrimingMode.Disabled, 2, outcomes.Add);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = ignoreWas;
            }

            var log = new StringBuilder();
            log.AppendLine($"[S13] pipeline asset MSAA {assetMsaa}x");
            foreach (var o in outcomes) log.AppendLine("[S13] " + o);
            Debug.Log(log.ToString().TrimEnd());

            var failures = new StringBuilder();
            foreach (var o in outcomes)
            {
                if (o.recorded == 0)
                {
                    failures.AppendLine($"{o.name}: the probe never recorded, so nothing was measured.");
                    continue;
                }
                if (o.msaaSeen != o.msaaAsked)
                {
                    // Not a failure of the package: the camera did not render at the
                    // sample count the row asked for, so the configuration was not
                    // reached. The asset has to allow MSAA for a target to be sampled.
                    Assert.Inconclusive(
                        $"{o.name}: the camera rendered at MSAA {o.msaaSeen}x, so this "
                      + $"configuration was not reached ({o}). A camera only multisamples "
                      + "its target when the pipeline asset allows MSAA at all, and the "
                      + "platform has to support the count on that target.");
                }
                bool primedWithMsaa = o.priming == DepthPrimingMode.Forced && o.msaaAsked > 1;
                if (primedWithMsaa)
                {
                    // The configuration the reuse policy exists to refuse. What matters
                    // is that it is refused for a reason this row has seen, not guessed.
                    // Measured 2026-09-03 on Basis's URP: a Render Graph execution error
                    // every frame and a camera that rendered nothing, so VRSL's own
                    // texture is empty here too and the whole frame is the casualty.
                    bool unusable = o.errors.Count > 0 || o.urpWritten == 0;
                    if (!unusable)
                        failures.AppendLine($"{o.name}: URP produced a normals texture under priming with "
                                          + "MSAA, with no error, and it matched VRSL's on "
                                          + $"{o.both - o.differing} of {o.both} pixels. The reuse policy "
                                          + "refuses this configuration on the strength of it failing; if "
                                          + "it no longer fails, the policy is leaving a saving on the table.");
                    continue;
                }

                if (o.vrslWritten == 0)
                {
                    failures.AppendLine($"{o.name}: VRSL's own normals texture is empty, so the "
                                      + "row has nothing to compare URP's against.");
                    continue;
                }

                {
                    // URP can produce its texture here, and it must be the same data.
                    if (o.errors.Count > 0)
                        failures.AppendLine($"{o.name}: {o.errors.Count} error(s) logged while rendering; "
                                          + $"first: {o.errors[0]}");
                    if (!o.urpHandle)
                        failures.AppendLine($"{o.name}: URP produced no normals texture although Normal was requested.");
                    float budget = 0.001f * o.vrslWritten;
                    if (o.onlyVrsl > budget || o.onlyUrp > budget)
                        failures.AppendLine($"{o.name}: the two textures cover different pixels "
                                          + $"(only VRSL {o.onlyVrsl}, only URP {o.onlyUrp} of {o.vrslWritten}).");
                    if (o.differing > budget)
                        failures.AppendLine($"{o.name}: {o.differing} of {o.both} shared pixels differ by "
                                          + $"more than {Tolerance}/255 (max {o.maxDiff}). Same tags, same "
                                          + "format, so a difference is a space or encoding, not noise.");
                }
            }

            if (failures.Length > 0) Assert.Fail(failures.ToString().TrimEnd());
        }
    }
}
#endif
