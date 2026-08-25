// VRSLImageCompare is editor-only, and this assembly builds for players too, so
// these rows compile only where their helper exists.
#if UNITY_EDITOR
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The rows a person is otherwise asked to judge by looking at two frames.
    ///
    /// The default comparison is against this machine's own previous capture rather
    /// than a committed reference image: two GPUs do not produce identical frames,
    /// so a committed image is a false-failure machine everywhere except where it
    /// was made. Committed references are the second mode, reached through
    /// <c>VRSL_PERF_HOME</c>, and rows needing one skip with a message when it is
    /// unset — a row that goes red because an environment variable is missing
    /// teaches people to ignore red rows.
    /// </summary>
    class VRSLImageRegressionTests : VRSLDMXTest
    {
        /// <summary>Bigger than the correctness rows use. A beam a few pixels across
        /// cannot show a one-pixel difference, and these rows are about differences.</summary>
        const int ImageSize = 512;

        /// <summary>Frames before a capture. Movement damping converges rather than
        /// settling instantly, so a frame taken too early differs from the same frame
        /// taken later for reasons that are not the change under test.</summary>
        const int WarmUpFrames = 120;

        /// <summary>
        /// Build a rig, freeze everything that moves on its own, render to a fixed
        /// frame index, and read the frame back.
        /// </summary>
        static IEnumerator CaptureFrame(System.Action<VRSLDMXRig> configure,
                                        System.Action<Texture2D> onCaptured)
        {
            using var rig = VRSLDMXRig.Build(targetSize: ImageSize);
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
            rig.Manager.enabled = false;
            rig.Manager.enabled = true;
            configure?.Invoke(rig);

            // After configure, so a caller that bounced the manager does not undo it.
            rig.FreezeForImageCapture();

            for (int i = 0; i < WarmUpFrames; i++)
            {
                yield return null;
                rig.RenderFrame();
            }

            onCaptured(VRSLImageCompare.Read(rig.Target));
        }

        /// <summary>Where a failing row leaves its evidence.</summary>
        static string EvidenceFolder =>
            Path.Combine(Directory.GetParent(Application.dataPath)!.FullName,
                         "VRSL-Benchmarks", "image-failures");

        /// <summary>
        /// P1. Clearing the cull shader must not change a single pixel.
        ///
        /// The cull is an acceleration: it decides which lights a tile iterates, never
        /// what they contribute. Any visual change at all means it is dropping a light
        /// that reaches the tile, and the frametime win is being paid for in wrong
        /// pixels. This is the row that made the cull trustworthy enough to build M3 on.
        /// </summary>
        [UnityTest]
        public IEnumerator P1_ClearingTheCullShaderChangesNoPixels()
        {
            Texture2D culled = null, unculled = null;
            yield return CaptureFrame(null, t => culled = t);
            yield return CaptureFrame(rig =>
            {
                // The cull resolves its shader in its constructor and the manager drops
                // the pass on disable, so clearing the field takes only after a bounce.
                rig.Manager.lightCullShader = null;
                rig.Manager.enabled = false;
                rig.Manager.enabled = true;
            }, t => unculled = t);

            try
            {
                var result = VRSLImageCompare.Compare(culled, unculled);
                Debug.Log($"[P1] cull on vs off: {result}");

                Assert.IsFalse(result.SizeMismatch, "the two captures came back different sizes");

                if (result.Max > VRSLImageCompare.Threshold)
                {
                    VRSLImageCompare.WriteImages(EvidenceFolder, "P1-cull", culled, unculled);
                    Assert.Fail(
                        $"tile culling changed the image ({result}). The cull decides which "
                      + "lights a tile iterates, never what they contribute, so any visual "
                      + "difference means it is dropping a light that reaches the tile. "
                      + $"Images written to {EvidenceFolder}");
                }
            }
            finally
            {
                if (culled != null)   Object.DestroyImmediate(culled);
                if (unculled != null) Object.DestroyImmediate(unculled);
            }
        }

        /// <summary>
        /// A-M0-4, the sensitivity half. The comparison has to catch a one-pixel
        /// offset in real rendered content, which is the fault the criterion names.
        ///
        /// Seeded by shifting a captured frame rather than by putting a debug keyword
        /// in the volumetric shader: the claim being made is about what the comparator
        /// can resolve, and this exercises exactly that without adding surface to
        /// shipped code or a variant to compile.
        /// </summary>
        [UnityTest]
        public IEnumerator A_M0_4_TheComparisonCatchesAOnePixelOffset()
        {
            Texture2D frame = null;
            yield return CaptureFrame(null, t => frame = t);

            Texture2D shifted = null;
            try
            {
                shifted = VRSLImageCompare.ShiftedByOnePixel(frame);
                var result = VRSLImageCompare.Compare(frame, shifted);
                Debug.Log($"[A-M0-4] one-pixel shift: {result}");

                Assert.Greater(result.Max, VRSLImageCompare.Threshold,
                    $"a one-pixel offset of a real frame was not detected ({result}). "
                  + "The comparison cannot resolve the fault A-M0-4 names, so it cannot "
                  + "be trusted to catch an upsample or a tile-index error either");

                // A shift is not a global shift: most of a frame is smooth and moves
                // barely at all, so demanding a large percentile here would be demanding
                // the wrong thing. What must be true is that some pixels moved a lot.
                Assert.Greater(result.DifferingPixels, 0,
                    "no pixel differed by more than the quantisation threshold");
            }
            finally
            {
                if (frame != null)   Object.DestroyImmediate(frame);
                if (shifted != null) Object.DestroyImmediate(shifted);
            }
        }

        /// <summary>
        /// A-M0-4, the specificity half. The comparison must call two captures of an
        /// unchanged scene identical.
        ///
        /// Without this the sensitivity row above is satisfied by a comparator that
        /// calls everything different, which is exactly as useless as one that calls
        /// everything the same. Two renders are not bit-identical — the GPU may
        /// reorder floating-point work between draws — so the row is what says the
        /// threshold is set somewhere defensible.
        /// </summary>
        [UnityTest]
        public IEnumerator A_M0_4_TwoCapturesOfAnUnchangedSceneAreIdentical()
        {
            Texture2D first = null, second = null;
            yield return CaptureFrame(null, t => first = t);
            yield return CaptureFrame(null, t => second = t);

            try
            {
                var result = VRSLImageCompare.Compare(first, second);
                Debug.Log($"[A-M0-4] unchanged scene, twice: {result}");

                if (result.Max > VRSLImageCompare.Threshold)
                {
                    VRSLImageCompare.WriteImages(EvidenceFolder, "A-M0-4-unchanged", first, second);
                    Assert.Fail(
                        $"two captures of an unchanged scene differ ({result}). Either the "
                      + "capture is not deterministic — something is still integrating over "
                      + "time — or the threshold is too tight to survive ordinary "
                      + $"floating-point reordering. Images written to {EvidenceFolder}");
                }
            }
            finally
            {
                if (first != null)  Object.DestroyImmediate(first);
                if (second != null) Object.DestroyImmediate(second);
            }
        }

        /// <summary>
        /// The default mode: this machine's own previous capture. Seeds on the first
        /// run and compares on every one after, which is how the tool is actually
        /// used — land a change, see what moved.
        /// </summary>
        [UnityTest]
        public IEnumerator I1_TheFrameMatchesThisMachinesPreviousCapture()
        {
            Texture2D frame = null;
            yield return CaptureFrame(null, t => frame = t);

            Texture2D previous = null;
            try
            {
                string path = VRSLImageCompare.LocalPath("rig-default");
                previous = VRSLImageCompare.Load(path);

                if (previous == null)
                {
                    VRSLImageCompare.Save(path, frame);
                    Assert.Inconclusive(
                        $"no previous capture on this machine, so this run seeded one at {path}. "
                      + "Run the row again and it compares against it.");
                }

                var result = VRSLImageCompare.Compare(previous, frame);
                Debug.Log($"[I1] against previous local capture: {result}");

                if (result.Max > VRSLImageCompare.Threshold)
                {
                    VRSLImageCompare.WriteImages(EvidenceFolder, "I1-local", previous, frame);
                    Assert.Fail(
                        $"the frame moved since the last capture on this machine ({result}). "
                      + "If the change was intended, delete the stored image to re-seed it: "
                      + $"{path}. Images written to {EvidenceFolder}");
                }
            }
            finally
            {
                if (frame != null)    Object.DestroyImmediate(frame);
                if (previous != null) Object.DestroyImmediate(previous);
            }
        }

        /// <summary>
        /// The second mode: a committed reference frame from the programme repo,
        /// reached through <c>VRSL_PERF_HOME</c>. Only the reference machine and any
        /// future CI take this path; everywhere else it skips.
        /// </summary>
        [UnityTest]
        public IEnumerator I2_TheFrameMatchesTheCommittedReference()
        {
            string golden = VRSLImageCompare.GoldenFolder;
            if (golden == null)
                Assert.Ignore("VRSL_PERF_HOME is not set, or has no golden/ folder. A "
                            + "consuming project has no reason to hold one machine's "
                            + "reference frames, so this row is for the reference machine.");

            Texture2D frame = null, reference = null;
            yield return CaptureFrame(null, t => frame = t);

            try
            {
                string path = Path.Combine(golden, "rig-default.png");
                reference = VRSLImageCompare.Load(path);
                if (reference == null)
                {
                    VRSLImageCompare.Save(path, frame);
                    Assert.Inconclusive($"no reference frame yet; seeded one at {path}. "
                                      + "Commit it to the programme repo if it looks right.");
                }

                var result = VRSLImageCompare.Compare(reference, frame);
                Debug.Log($"[I2] against committed reference: {result}");

                if (result.Max > VRSLImageCompare.Threshold)
                {
                    VRSLImageCompare.WriteImages(EvidenceFolder, "I2-reference", reference, frame);
                    Assert.Fail(
                        $"the frame differs from the committed reference ({result}). On any "
                      + "machine but the one that made it, expect this — two GPUs do not "
                      + $"render identically. Images written to {EvidenceFolder}");
                }
            }
            finally
            {
                if (frame != null)     Object.DestroyImmediate(frame);
                if (reference != null) Object.DestroyImmediate(reference);
            }
        }
    }
}

#endif
