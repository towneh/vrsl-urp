using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The depth rows that can be judged without a person looking at a scene.
    ///
    /// Depth priming is the setting that decides whether a fixture renders at all: with
    /// it on, URP draws opaque geometry with an <c>Equal</c> depth test against a
    /// prepass, so a shader whose depth passes do not reproduce its forward pass is
    /// culled from the frame entirely. The failure is geometry that silently does not
    /// draw, which is why it is worth a row rather than an eyeball.
    /// </summary>
    class VRSLDepthPolicyTests : VRSLDMXTest
    {
        const int ImageSize    = 512;
        const int WarmUpFrames = 120;

        /// <summary>Render a frame with the renderer's priming set as given.</summary>
        static IEnumerator CaptureAtPriming(DepthPrimingMode mode, System.Action<Texture2D> onCaptured)
        {
            using var rig = VRSLDMXRig.Build(targetSize: ImageSize);
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
            rig.Manager.enabled = false;
            rig.Manager.enabled = true;
            rig.FreezeForImageCapture();

            for (int i = 0; i < WarmUpFrames; i++)
            {
                yield return null;
                rig.RenderFrame();
            }

            onCaptured(VRSLImageCompare.Read(rig.Target));
        }

        /// <summary>Every Universal Renderer on the active pipeline asset.</summary>
        static List<UniversalRendererData> Renderers()
        {
            var found = new List<UniversalRendererData>();
            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset urp)
                return found;
            foreach (var data in urp.rendererDataList)
                if (data is UniversalRendererData universal) found.Add(universal);
            return found;
        }

        /// <summary>
        /// D1 and D2 together. The image must not depend on depth priming.
        ///
        /// This is the milestone's central claim, and the reason it is one row rather
        /// than two: what matters is not that each configuration renders something, but
        /// that they render the <i>same</i> thing. A shader whose depth passes disagree
        /// with its forward pass draws under `Disabled` and vanishes under `Forced`, and
        /// only a comparison catches that.
        ///
        /// <b>What this row cannot tell you</b> is whether URP honoured a priming change
        /// made mid-session. It asserts the field read back, and that neither frame went
        /// dark — a fixture culled by a mismatched depth pass would take the frame with
        /// it — but the authoritative check is still D1 and D2 by hand on a renderer
        /// configured before play. Treat this as the row that catches a regression, not
        /// as the one that proved it worked the first time.
        /// </summary>
        [UnityTest]
        public IEnumerator D1_D2_TheImageDoesNotDependOnDepthPriming()
        {
            var renderers = Renderers();
            if (renderers.Count == 0)
                Assert.Ignore("No Universal Renderer on the active pipeline asset, so there "
                            + "is no priming setting to vary.");

            var restore = new Dictionary<UniversalRendererData, DepthPrimingMode>();
            foreach (var r in renderers) restore[r] = r.depthPrimingMode;

            Texture2D disabled = null, forced = null;
            try
            {
                foreach (var r in renderers) r.depthPrimingMode = DepthPrimingMode.Disabled;
                yield return null;
                foreach (var r in renderers)
                    Assert.AreEqual(DepthPrimingMode.Disabled, r.depthPrimingMode,
                        $"'{r.name}' did not accept a priming change, so this row varied nothing");
                yield return CaptureAtPriming(DepthPrimingMode.Disabled, t => disabled = t);

                foreach (var r in renderers) r.depthPrimingMode = DepthPrimingMode.Forced;
                yield return null;
                yield return CaptureAtPriming(DepthPrimingMode.Forced, t => forced = t);

                // Neither frame may be empty. A fixture culled by a depth pass that
                // disagrees with its forward pass takes its geometry out of the frame, so
                // "both are black" would otherwise compare as a pass.
                AssertLit(disabled, "priming Disabled");
                AssertLit(forced, "priming Forced");

                var result = VRSLImageCompare.Compare(disabled, forced);
                Debug.Log($"[D1/D2] Disabled vs Forced: {result}");

                // Judged on whether geometry disappeared, not on bit-equality.
                //
                // Depth priming draws opaque geometry with an Equal test against a
                // prepass, and the two vertex transforms are separate evaluations of the
                // same maths — so a scatter of edge pixels failing that test is expected
                // and benign. Measured 2026-08-24: 615 pixels of 262144 differing by at
                // most 9 of 255, with the lit fraction moving 63.0% to 62.9%.
                //
                // What this row exists to catch looks nothing like that. A fixture whose
                // depth pass disagrees with its forward pass loses its whole body: a
                // contiguous region, thousands of pixels, going to background. So the
                // bar is the lit fraction and a budget on how much may differ, which is
                // the requirement's own language — "renders correctly" — rather than a
                // pixel-exactness the requirement never asked for.
                float litDisabled = LitFraction(disabled);
                float litForced   = LitFraction(forced);
                Debug.Log($"[D1/D2] lit {litDisabled:F2}% against {litForced:F2}%");

                Assert.AreEqual(litDisabled, litForced, 0.5f,
                    $"the lit area of the frame changed with depth priming "
                  + $"({litDisabled:F2}% against {litForced:F2}%). Geometry is appearing or "
                  + "disappearing, which is what a depth pass disagreeing with its forward "
                  + "pass does. Run VRSL → URP → Validate Renderer Setup to see which shader.");

                const float EdgeBudget = 1.0f;
                if (result.DifferingPercent > EdgeBudget)
                {
                    VRSLImageCompare.WriteImages(
                        System.IO.Path.Combine(
                            System.IO.Directory.GetParent(Application.dataPath)!.FullName,
                            "VRSL-Benchmarks", "image-failures"),
                        "D1-D2-priming", disabled, forced);
                    Assert.Fail(
                        $"depth priming changed {result.DifferingPercent:F2}% of the image, "
                      + $"past the {EdgeBudget}% allowed for depth-test precision at geometry "
                      + $"edges ({result}). Under priming URP draws opaque geometry with an "
                      + "Equal depth test against a prepass, so a shader whose depth passes do "
                      + "not reproduce its forward pass is culled from the frame. Run "
                      + "VRSL → URP → Validate Renderer Setup to see which one.");
                }
            }
            finally
            {
                foreach (var pair in restore)
                    if (pair.Key != null) pair.Key.depthPrimingMode = pair.Value;
                if (disabled != null) Object.DestroyImmediate(disabled);
                if (forced != null)   Object.DestroyImmediate(forced);
            }
        }

        /// <summary>
        /// D5. No light of any kind in the scene, and surfaces still light.
        ///
        /// Depth comes from the pipeline, not from a scene light. The Built-in pipeline
        /// did need one present, which is where VRSL's old depth-light prefab and its
        /// requirement toggle came from; neither means anything under URP and both are
        /// gone. This row is what stops them coming back by accident.
        /// </summary>
        [UnityTest]
        public IEnumerator D5_ASceneWithNoLightsStillRenders()
        {
            Texture2D frame = null;
            yield return CaptureAtPriming(DepthPrimingMode.Disabled, t => frame = t);

            try
            {
                var lights = Object.FindObjectsByType<Light>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                Assert.IsEmpty(lights,
                    "the scene has a light in it, so this row cannot claim that VRSL renders "
                  + "without one. The rig builds no lights; something else added it");

                AssertLit(frame, "a scene with no lights");
            }
            finally { if (frame != null) Object.DestroyImmediate(frame); }
        }

        /// <summary>Percentage of the frame carrying any light at all.</summary>
        static float LitFraction(Texture2D frame)
        {
            var pixels = frame.GetPixels32();
            int lit = 0;
            foreach (var p in pixels)
                if (p.r > 8 || p.g > 8 || p.b > 8) lit++;
            return 100f * lit / pixels.Length;
        }

        /// <summary>A frame with nothing lit in it compares equal to any other such
        /// frame, so every row here has to rule that out before believing itself.</summary>
        static void AssertLit(Texture2D frame, string what)
        {
            float percent = LitFraction(frame);
            Debug.Log($"[depth] {what}: {percent:F1}% of pixels lit");
            Assert.Greater(percent, 1f,
                $"{what} rendered an essentially black frame ({percent:F2}% lit), so nothing "
              + "was drawn and any comparison against it is meaningless");
        }
    }
}
