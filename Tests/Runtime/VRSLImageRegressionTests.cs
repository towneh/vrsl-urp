// VRSLImageCompare is editor-only, and this assembly builds for players too, so
// these rows compile only where their helper exists.
#if UNITY_EDITOR
using System.Collections;
using System.Linq;
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
            // Every dimmer at full with mid-range colours, held still. Under Ramp the
            // fixtures in view carry dimmer and colour channels of a few percent, and
            // the frame was nearly black at any intensity.
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
            rig.Source.speed   = 0f;
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
        /// A-M7-1. A manager with every wiring field cleared repairs itself and renders
        /// the same image as one wired by the prefab.
        ///
        /// This is the row M7 lives or dies on, and it is the only one that can make the
        /// claim. Comparing GUIDs proves resolution picked the assets the table names;
        /// it cannot prove the table names the right ones. Rendering proves it, because
        /// a wrong asset in any of the ten shows up — the wrong compute lights nothing,
        /// the wrong lighting shader puts nothing on surfaces, the wrong prepass makes
        /// every surface flat grey.
        ///
        /// Bounced twice on purpose. Clearing takes effect only after a bounce, since
        /// the cull resolves its shader in its constructor and the manager builds its
        /// volumetric material once and keeps it; and the resolved values need a second
        /// bounce for the same reason. Without the first, the row would compare two
        /// correctly wired managers and pass whatever resolution did.
        /// </summary>
        [UnityTest]
        public IEnumerator A_M7_1_AClearedManagerRepairsItselfAndRendersIdentically()
        {
            Texture2D wired = null, repaired = null;
            yield return CaptureFrame(null, t => wired = t);
            yield return CaptureFrame(rig =>
            {
                var so = new UnityEditor.SerializedObject(rig.Manager);
                foreach (var field in VRSLWiring.Dmx)
                    so.FindProperty(field.Field).objectReferenceValue = null;
                so.ApplyModifiedProperties();

                // Clearing the fields is not enough, and this row passed with a
                // deliberately wrong shader until it did this. The manager builds its
                // lighting and volumetric materials once, from whatever the fields held
                // at the time, and never drops them — so both survived every bounce, the
                // capture kept rendering through the prefab's own materials, and the row
                // could not see resolution's answer at all. Destroying them makes the
                // manager's references fake-null so the next enable rebuilds from
                // whatever resolution put back.
                DropMaterials(rig.Manager);

                rig.Manager.enabled = false;
                rig.Manager.enabled = true;

                // The enable path fills what must never be empty, and nothing else. The
                // four that document a meaning when empty — no tile culling, no surface
                // prepass, no beams, no strobe timing — are a choice an author is allowed
                // to make, so they are offered rather than forced.
                var afterEnable = VRSLWiring.Empty(rig.Manager);
                Assert.IsEmpty(afterEnable.Where(f => !f.Optional).Select(f => f.Field),
                    "enabling the manager left a mandatory field empty");

                // And the other half of that claim, which the line above cannot make:
                // the optional ones must still be empty. Without this the row would pass
                // just as happily if enabling filled everything, and the whole point of
                // holding those back is that it does not.
                Assert.AreEqual(VRSLWiring.Dmx.Count(f => f.Optional),
                                afterEnable.Count(f => f.Optional),
                    "enabling the manager filled a field that is only filled on request");

                // Then the repair an author presses, which is what fills the rest. Both
                // paths are exercised, and the capture needs all ten or it would be
                // comparing a scene with no volumetrics against one that has them.
                VRSLWiring.Resolve(rig.Manager, includeOptional: true);
                DropMaterials(rig.Manager);
                rig.Manager.enabled = false;
                rig.Manager.enabled = true;

                var empty = VRSLWiring.Empty(rig.Manager);
                Assert.IsEmpty(empty.Select(f => f.Field),
                    "the manager still has empty wiring, so this row would be comparing a "
                  + "half-wired manager against a wired one rather than a repaired one");

            }, t => repaired = t);

            try
            {
                var result = VRSLImageCompare.Compare(wired, repaired);
                Debug.Log($"[A-M7-1] prefab wiring vs repaired: {result}");

                Assert.IsFalse(result.SizeMismatch, "the two captures came back different sizes");

                if (result.Max > VRSLImageCompare.Threshold)
                {
                    VRSLImageCompare.WriteImages(EvidenceFolder, "A-M7-1-wiring", wired, repaired);
                    Assert.Fail(
                        $"a repaired manager renders differently from a wired one ({result}). "
                      + "Resolution picked assets, but not the same ones the prefab uses — "
                      + "which is the fault this milestone would otherwise introduce, since "
                      + "a manager wired to the wrong assets fails exactly as silently as one "
                      + $"nobody wired. Images written to {EvidenceFolder}");
                }
            }
            finally
            {
                if (wired != null)    Object.DestroyImmediate(wired);
                if (repaired != null) Object.DestroyImmediate(repaired);
            }
        }

        /// <summary>
        /// Destroy the materials the manager built, so its next enable builds them again
        /// from whatever the shader fields hold now.
        ///
        /// DestroyImmediate rather than Destroy: a test advances frames itself and the
        /// deferred destruction would land after the bounce that is meant to see it.
        /// </summary>
        static void DropMaterials(VRSL_URPLightManager manager)
        {
            if (manager.LightingMaterial   != null) Object.DestroyImmediate(manager.LightingMaterial);
            if (manager.VolumetricMaterial != null) Object.DestroyImmediate(manager.VolumetricMaterial);
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
                string path = VRSLImageCompare.LocalPath(VRSLDMXRig.CaptureName("rig-default"));
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

                // Said separately from the budget below, which would otherwise report a
                // mismatch as the whole frame having moved. The cause is almost always a
                // changed capture size rather than anything in the picture.
                Assert.IsFalse(result.SizeMismatch,
                    "the stored capture is a different size to this frame, so nothing was "
                  + $"compared. Delete it to re-seed at the current size: {path}");

                // Judged on how much of the frame moved, not on bit-equality.
                //
                // A bit-exact bar has now failed twice for reasons that are not
                // regressions. Depth priming draws opaque geometry with an Equal test
                // against a prepass, so edge pixels scatter whenever priming state
                // differs — and the D1/D2 row deliberately changes priming and runs
                // immediately before these, leaving 159 pixels of 262144 differing at up
                // to 24 of 255. A real regression is not that shape: a changed shader or
                // a broken cull moves large contiguous areas.
                //
                // The budget is set well above what has been observed as benign (0.06%)
                // and well below the unexplained run-shape difference (0.51%), which
                // therefore still fails. That one is a genuine open question and should
                // stay visible rather than be absorbed by a tolerance.
                const float EdgeBudget = 0.25f;
                if (result.DifferingPercent > EdgeBudget)
                {
                    VRSLImageCompare.WriteImages(EvidenceFolder, "I1-local", previous, frame);
                    Assert.Fail(
                        $"{result.DifferingPercent:F2}% of the frame moved since the last "
                      + $"capture on this machine, past the {EdgeBudget}% allowed for edge "
                      + $"scatter ({result}). If the change was intended, delete the stored "
                      + $"image to re-seed it: {path}. Images written to {EvidenceFolder}");
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
                string path = Path.Combine(golden, VRSLDMXRig.CaptureName("rig-default") + ".png");
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
