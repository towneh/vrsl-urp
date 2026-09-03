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
    /// The surface prepass rows: what it draws, for whom, and where its normals
    /// may come from instead.
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
        /// through the probe shader: pass 0 reads <c>_VRSLNormalsTexture</c>, pass 1
        /// reads <c>_CameraNormalsTexture</c>.
        /// </summary>
        sealed class ProbePass : ScriptableRenderPass
        {
            class Data
            {
                public TextureHandle vrsl, urp;
                public Material      material;
            }

            readonly Material _material;
            readonly RTHandle _vrsl, _urp;

            public int  ObservedMsaa   { get; private set; }
            public bool UrpHandleValid { get; private set; }
            public int  Recorded       { get; private set; }

            public ProbePass(Material material, RTHandle vrsl, RTHandle urp)
            {
                _material = material;
                _vrsl     = vrsl;
                _urp      = urp;
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
                // Normal is what turns URP's prepass from depth-only into
                // depth-and-normals. Depth is what the package's passes ask for.
                ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
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
                    cmd.DrawProcedural(Matrix4x4.identity, p.material, 1, MeshTopology.Triangles, 3);
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

            var restore = new Dictionary<UniversalRendererData, DepthPrimingMode>();
            foreach (var r in Renderers()) restore[r] = r.depthPrimingMode;
            foreach (var r in restore.Keys) r.depthPrimingMode = mode;

            // A camera with a target takes its sample count from the target, but only
            // when the pipeline asset allows MSAA at all, and a host that rewrites its
            // asset per quality tier does so at a moment of its own: the asset read 2x
            // as this row began and 1x by the time its first frame rendered. Held to
            // the asked count for the configuration's frames, and put back after.
            var asset   = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            int msaaWas = asset != null ? asset.msaaSampleCount : 0;
            void HoldAssetMsaa()
            {
                if (asset != null && msaa > 1 && asset.msaaSampleCount != msaa)
                    asset.msaaSampleCount = msaa;
            }

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
                    HoldAssetMsaa();
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
                if (asset != null && msaaWas > 0) asset.msaaSampleCount = msaaWas;
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
