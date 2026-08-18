using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Strobe, judged on the active flag rather than on printed intensity.
    /// Under Ramp many of these fixtures hold low dimmers and are genuinely lit
    /// while rounding to zero, so intensity is the wrong observable.
    ///
    /// Rows N9 and N10 of TESTING.md.
    /// </summary>
    class VRSLDMXStrobeTests : VRSLDMXTest
    {
        enum Bucket { Held, Medium, High }

        // Speed selection is `dmx > 0.2 ? med : low` and the output stage is
        // `status > 0.2 ? square : 1`, so anything at or below 0.2 is held fully
        // on and never strobes. Above 0.5 selects the high rate.
        static Bucket BucketOf(int absChannel)
        {
            float dmx = VRSLDMXRig.RampAt(VRSLDMXRig.StrobeChannel(absChannel));
            return dmx <= 0.2f ? Bucket.Held : dmx <= 0.5f ? Bucket.Medium : Bucket.High;
        }

        // active also drops to 0 when a fixture is too dim to emit at all, which
        // is a different fault from being strobed off and has to be excluded
        // before the flag means anything.
        static bool TooDim(VRSL_URPLightManager.VRSLLightData d)
            => Mathf.Max(d.colorAndIntensity.x, Mathf.Max(d.colorAndIntensity.y, d.colorAndIntensity.z))
               <= 6f / 255f;

        static bool Active(VRSL_URPLightManager.VRSLLightData d) => d.colorAndIntensity.w > 0f;

        /// <summary>Consecutive frames, so the sample rate cannot alias with the
        /// strobe rate: the fastest bucket runs at 65 rad/s, a period of about
        /// six frames.</summary>
        static IEnumerator Sample(VRSLDMXRig rig, int count, List<VRSL_URPLightManager.VRSLLightData[]> into)
        {
            for (int i = 0; i < count; i++)
            {
                into.Add(rig.ReadLights());
                // Stepped here rather than through rig.Step: this helper is already
                // yielded from the test, and the runner does not drive a second level.
                yield return null;
                rig.RenderFrame();
            }
        }

        [UnityTest]
        public IEnumerator N9_each_bucket_strobes_together_and_the_buckets_differ()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern    = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes  = 4;
                rig.Manager.disableStrobe = false;
                yield return rig.Step(5);

                var samples = new List<VRSL_URPLightManager.VRSLLightData[]>();
                yield return Sample(rig, 24, samples);

                var groups = Enumerable.Range(0, VRSLDMXRig.FixtureCount)
                                       .Where(i => !TooDim(samples[0][i]))
                                       .GroupBy(i => BucketOf(VRSLDMXRig.ChannelOf(i)))
                                       .ToDictionary(g => g.Key, g => g.ToArray());

                foreach (var bucket in new[] { Bucket.Held, Bucket.Medium, Bucket.High })
                    Assert.IsTrue(groups.ContainsKey(bucket) && groups[bucket].Length > 0,
                        $"no fixture landed in the {bucket} bucket, so this row cannot judge it");

                foreach (int i in groups[Bucket.Held])
                    for (int s = 0; s < samples.Count; s++)
                        Assert.IsTrue(Active(samples[s][i]),
                            $"ch {VRSLDMXRig.ChannelOf(i)} is at or below the 0.2 threshold and must "
                          + $"be held fully on, but read off in sample {s}");

                foreach (var bucket in new[] { Bucket.Medium, Bucket.High })
                {
                    var members = groups[bucket];
                    var seen = new HashSet<bool>();
                    for (int s = 0; s < samples.Count; s++)
                    {
                        var states = members.Select(i => Active(samples[s][i])).Distinct().ToArray();
                        Assert.AreEqual(1, states.Length,
                            $"the {bucket} bucket disagreed with itself in sample {s}; every fixture "
                          + "in a bucket shares one phase, wherever it sits on the truss");
                        seen.Add(states[0]);
                    }
                    Assert.AreEqual(2, seen.Count,
                        $"the {bucket} bucket never changed state across {samples.Count} frames, so "
                      + "it is not strobing at all");
                }

                // The evidence ten fixtures could not produce: if both buckets
                // shared one phase they would never disagree, and the row would
                // pass without the two rates ever being distinguished.
                bool disagreed = Enumerable.Range(0, samples.Count).Any(s =>
                    Active(samples[s][groups[Bucket.Medium][0]]) !=
                    Active(samples[s][groups[Bucket.High][0]]));
                Assert.IsTrue(disagreed,
                    "the medium and high buckets never disagreed, so nothing here shows they run "
                  + "at different rates rather than sharing one phase");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator N10_the_global_toggle_holds_the_multiplier_at_one()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 4;
                rig.Manager.disableStrobe = true;
                yield return rig.Step(5);

                var samples = new List<VRSL_URPLightManager.VRSLLightData[]>();
                yield return Sample(rig, 8, samples);

                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                {
                    if (TooDim(samples[0][i])) continue;
                    for (int s = 0; s < samples.Count; s++)
                    {
                        Assert.IsTrue(Active(samples[s][i]),
                            $"ch {VRSLDMXRig.ChannelOf(i)} read off in sample {s} with strobe disabled");
                        // The stronger half: a toggle that merely forced the active
                        // flag would still let the multiplier move underneath it.
                        Assert.AreEqual(samples[0][i].colorAndIntensity.w,
                                        samples[s][i].colorAndIntensity.w, 1e-5f,
                            $"ch {VRSLDMXRig.ChannelOf(i)} changed intensity between samples, so the "
                          + "toggle is not holding the strobe multiplier at exactly 1");
                    }
                }
            }
            finally { rig.Dispose(); }
        }
    }
}
