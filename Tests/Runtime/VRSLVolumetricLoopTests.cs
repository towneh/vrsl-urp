using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows for the volumetric inner loop: the baked density field, the counters
    /// the raymarch reports, the step count following the span and the level, and
    /// the visibility bound reaching the loop.
    ///
    /// Rows V14 to V17 of TESTING.md. None of them looks at the image: the step
    /// count and the bound are both designed to leave it alone, which is why the
    /// counters exist.
    /// </summary>
    class VRSLVolumetricLoopTests : VRSLDMXTest
    {
        const int Size = VRSLVolumetricNoise.Size;

        static int Index(int x, int y, int z) => x + Size * (y + Size * z);

        /// <summary>Every texel of the baked field, x fastest.</summary>
        static byte[] ReadBack(RenderTexture rt)
        {
            var request = AsyncGPUReadback.Request(rt, 0, TextureFormat.R8);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, "the readback of the noise texture failed");

            var texels = new byte[Size * Size * Size];
            int layers = request.layerCount;
            if (layers == Size)
            {
                for (int z = 0; z < Size; z++)
                    Unity.Collections.NativeArray<byte>.Copy(
                        request.GetData<byte>(z), 0, texels, z * Size * Size, Size * Size);
            }
            else if (layers == 1 && request.GetData<byte>(0).Length == texels.Length)
            {
                request.GetData<byte>(0).CopyTo(texels);
            }
            else
            {
                Assert.Fail($"the readback came back as {layers} layer(s) of "
                          + $"{request.GetData<byte>(0).Length} bytes, which is not {Size} cubed");
            }
            return texels;
        }

        /// <summary>Mean absolute difference between texels one step apart along
        /// <paramref name="axis"/>, either across the interior or across the wrap
        /// from the last texel back to the first.</summary>
        static float NeighbourDifference(byte[] t, int axis, bool acrossWrap)
        {
            double sum = 0; long n = 0;
            for (int a = 0; a < Size; a++)
            for (int b = 0; b < Size; b++)
            {
                int steps = acrossWrap ? 1 : Size - 1;
                for (int i = 0; i < steps; i++)
                {
                    int from = acrossWrap ? Size - 1 : i;
                    int to   = acrossWrap ? 0        : i + 1;
                    int p = axis == 0 ? Index(from, a, b) : axis == 1 ? Index(a, from, b) : Index(a, b, from);
                    int q = axis == 0 ? Index(to,   a, b) : axis == 1 ? Index(a, to,   b) : Index(a, b, to);
                    sum += Mathf.Abs(t[p] - t[q]);
                    n++;
                }
            }
            return (float)(sum / n) / 255f;
        }

        [Test]
        public void V14_the_baked_field_varies_and_tiles_without_a_seam()
        {
            var rig = VRSLDMXRig.Build(withSource: false);
            Texture texture = null;
            try
            {
                texture = VRSLVolumetricNoise.Bake(rig.Manager.computeShader, rig.Manager);
                Assert.IsTrue(VRSLVolumetricNoise.IsBaked(texture),
                    "the package's own compute should be able to bake the field");
                var rt = (RenderTexture)texture;
                Assert.AreEqual(TextureDimension.Tex3D, rt.dimension);
                Assert.AreEqual(Size, rt.width);
                Assert.AreEqual(Size, rt.height);
                Assert.AreEqual(Size, rt.volumeDepth);
                Assert.AreEqual(TextureWrapMode.Repeat, rt.wrapMode,
                    "a field that does not wrap shows its seam wherever a beam crosses it");

                var texels = ReadBack(rt);

                // A field rather than a constant or a ramp: it spans most of the range
                // and sits around the middle, which is what value noise does.
                int min = 255, max = 0; double mean = 0;
                foreach (var v in texels) { if (v < min) min = v; if (v > max) max = v; mean += v; }
                mean /= texels.Length * 255.0;
                Assert.Less(min / 255f, 0.25f, "the darkest texel is not dark");
                Assert.Greater(max / 255f, 0.75f, "the brightest texel is not bright");
                Assert.That(mean, Is.InRange(0.35, 0.65), "the mean is off centre for value noise");

                // Seamless. Interior neighbours differ by a small amount, because four
                // texels span each lattice cell; two unrelated lattice points differ by
                // about a third of the range. A non-periodic bake would put the second
                // figure at every wrap, so the wrap has to look like the interior.
                for (int axis = 0; axis < 3; axis++)
                {
                    float interior = NeighbourDifference(texels, axis, acrossWrap: false);
                    float wrap     = NeighbourDifference(texels, axis, acrossWrap: true);
                    Assert.Greater(interior, 0f, $"axis {axis} is constant");
                    Assert.LessOrEqual(wrap, interior * 1.5f,
                        $"axis {axis}: texels across the wrap differ by {wrap:F4} on average against "
                      + $"{interior:F4} between interior neighbours, so the field does not tile");
                }
            }
            finally
            {
                if (texture != null) VRSLVolumetricNoise.Release(ref texture);
                rig.Dispose();
            }
        }

        [Test]
        public void V15_a_compute_without_the_kernel_gets_the_white_fallback_and_one_warning()
        {
            VRSLVolumetricNoise.ResetWarningForTests();
            LogAssert.Expect(LogType.Warning, new Regex("no BakeVolumetricNoise kernel"));

            var first = VRSLVolumetricNoise.Bake(null, null);
            Assert.IsFalse(VRSLVolumetricNoise.IsBaked(first));
            Assert.AreEqual(1, first.width);
            Assert.AreEqual(1, first.height);
            Assert.AreEqual(TextureWrapMode.Repeat, first.wrapMode);
            Assert.AreEqual(Color.white, ((Texture3D)first).GetPixel(0, 0, 0),
                "the fallback has to read 1 so density is left unmodulated rather than zeroed");

            // Once per session, not once per manager or per frame.
            var second = VRSLVolumetricNoise.Bake(null, null);
            Assert.AreSame(first, second, "the fallback is shared");
            LogAssert.NoUnexpectedReceived();

            // Releasing a fallback must not destroy the shared texture.
            Texture handle = second;
            VRSLVolumetricNoise.Release(ref handle);
            Assert.IsNull(handle);
            Assert.IsTrue(first != null, "the shared fallback was destroyed by a release");
        }

        /// <summary>Every counter, plus the first fixture's light data, so a failure
        /// says what the march saw rather than which assertion tripped.</summary>
        static string Describe(VRSLDMXRig rig, VRSLVolumetricStats s)
        {
            var lights = rig.ReadRaw();
            var first  = lights.Length > 0 ? lights[0] : default;
            return $"[valid {s.Valid}, frame {s.Frame}, pixels {s.Pixels}, marched {s.LightsMarched}, "
                 + $"steps {s.Steps}, skipped {s.LightsSkipped}; {lights.Length} light(s), first: "
                 + $"pos {first.positionAndRange}, dir {first.directionAndType}, "
                 + $"colour {first.colorAndIntensity}, spot {first.spotParams}; "
                 + $"density {rig.Manager.volumetricDensity}, intensity {rig.Manager.volumetricIntensity}, "
                 + $"fog {rig.Manager.coupleToSceneFog}, steps {rig.Manager.VolumetricStepParams}]";
        }

        /// <summary>
        /// Point every fixture straight down, as hung.
        ///
        /// The rig's movers pan and tilt to whatever their channels say, and under
        /// the synthetic pattern that sends the beams off sideways at shallow
        /// angles, mostly clear of the floor the camera looks at. Measured before
        /// this existed: of some ten thousand marched pixels, 326 rays crossed any
        /// cone, every one of them a grazing edge. A row about the march needs
        /// beams in the frame, and this is what puts them there.
        /// </summary>
        static void PointBeamsDown(VRSLDMXRig rig)
        {
            foreach (var fixture in rig.Fixtures) fixture.enablePanTilt = false;
            rig.Manager.MarkConfigDirty();
        }

        /// <summary>One frame of counters from the rig's manager. Two ticks from
        /// request to result; three frames leaves a margin.</summary>
        static IEnumerator Collect(VRSLDMXRig rig)
        {
            rig.Manager.VolumetricStats.Request();
            yield return null; rig.RenderFrame();
            yield return null; rig.RenderFrame();
            yield return null; rig.RenderFrame();
        }

        [UnityTest]
        public IEnumerator V16_the_step_count_stays_between_the_floor_and_the_level_ceiling_and_rises_with_the_level()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                PointBeamsDown(rig);
                yield return rig.Step(4);

                rig.Manager.quality = VRSLQuality.Standard;
                yield return Collect(rig);
                var standard = rig.Manager.VolumetricStats.Last;
                var level    = VRSLQualityLevel.For(VRSLQuality.Standard);

                Assert.IsTrue(standard.Valid, "no frame of counters came back " + Describe(rig, standard));
                Assert.Greater(standard.Pixels, 0, "no pixel had a surface behind it, so the floor is missing " + Describe(rig, standard));
                Assert.Greater(standard.LightsMarched, 0, "no light was marched in a lit rig " + Describe(rig, standard));
                Assert.GreaterOrEqual(standard.Steps, 4 * standard.LightsMarched,
                    "a light was marched with fewer than the floor of four steps");
                Assert.LessOrEqual(standard.Steps, (long)level.VolumetricMaxSteps * standard.LightsMarched,
                    $"a light was marched with more than the Standard ceiling of {level.VolumetricMaxSteps}");

                rig.Manager.quality = VRSLQuality.High;
                yield return rig.Step(2);
                yield return Collect(rig);
                var high      = rig.Manager.VolumetricStats.Last;
                var highLevel = VRSLQualityLevel.For(VRSLQuality.High);

                Assert.IsTrue(high.Valid && high.Frame > standard.Frame, "the High frame was not collected");
                Assert.GreaterOrEqual(high.Steps, 4 * high.LightsMarched);
                Assert.LessOrEqual(high.Steps, (long)highLevel.VolumetricMaxSteps * high.LightsMarched,
                    $"a light was marched with more than the High ceiling of {highLevel.VolumetricMaxSteps}");
                // The same spans at a finer spacing and a higher ceiling take more steps.
                // If they did not, the count is not following the level's spacing.
                Assert.Greater(high.StepsPerLight, standard.StepsPerLight,
                    $"High took {high.StepsPerLight:F2} steps per light against Standard's "
                  + $"{standard.StepsPerLight:F2}, so the count is not following the spacing");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator V17_a_light_that_cannot_reach_the_pixel_is_skipped_before_any_stepping()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                PointBeamsDown(rig);
                // Held still, so the two frames compared below see the same rig:
                // a strobing fixture is inactive on alternate frames and never
                // reaches the loop at all, and a moving one changes what it crosses.
                rig.FreezeForImageCapture();
                rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                yield return rig.Step(8);

                yield return Collect(rig);
                var lit = rig.Manager.VolumetricStats.Last;
                Assert.IsTrue(lit.Valid && lit.LightsMarched > 0, "the lit rig marched nothing " + Describe(rig, lit));

                // The global intensity scales what lands in the pixel, so at this
                // value no light's whole span can reach one 8-bit step and the bound
                // has to reject every one of them before a single step is taken.
                rig.Manager.volumetricIntensity = 1e-12f;
                yield return Collect(rig);
                var dark = rig.Manager.VolumetricStats.Last;

                Assert.IsTrue(dark.Valid && dark.Frame > lit.Frame, "the dark frame was not collected");
                Assert.Greater(dark.LightsSkipped, 0, "nothing was skipped, so the bound is not reaching the loop");
                Assert.AreEqual(0, dark.LightsMarched,
                    $"{dark.LightsMarched} light(s) were still marched with nothing to show for it");
                Assert.AreEqual(0, dark.Steps, "steps were taken on lights that cannot be seen");
                // The same lights reached the loop both times; only the verdict moved.
                long reachedLit  = lit.LightsMarched + lit.LightsSkipped;
                long reachedDark = dark.LightsMarched + dark.LightsSkipped;
                Assert.AreEqual(reachedLit, reachedDark,
                    $"{reachedLit} light(s) reached the loop lit and {reachedDark} dark; the "
                  + "intensity must move only the verdict, not what reaches the loop");

                rig.Manager.volumetricIntensity = 1f;
                yield return Collect(rig);
                var again = rig.Manager.VolumetricStats.Last;
                Assert.Greater(again.LightsMarched, 0, "the march did not come back when the intensity did");
            }
            finally { rig.Dispose(); }
        }
    }
}
