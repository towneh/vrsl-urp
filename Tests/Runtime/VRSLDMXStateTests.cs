using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The accumulator rows — spin, movement damping, and the per-universe step
    /// the damping advances by. All of them are functions of elapsed time, which
    /// is why they live or die on <see cref="VRSLDMXRig.FrameDelta"/> being a
    /// captured constant rather than whatever the machine managed.
    ///
    /// Rows N8, N11, N12 and N14 of TESTING.md.
    /// </summary>
    class VRSLDMXStateTests : VRSLDMXTest
    {
        const float Tau = 2f * Mathf.PI;

        /// <summary>The gobo spin rate a channel implies, in rad/s. The direction
        /// bit is folded out of the value, and below 0.01 the kernel resets phase
        /// to zero rather than holding it.</summary>
        static float? SpinRate(int absChannel)
        {
            float dmx = VRSL_SyntheticDMXChannelSource.RampValue(absChannel + 9) / 255f;
            if (dmx < 0.01f) return null;
            return dmx > 0.5f ? -4f * (dmx - 0.5f) : 4f * dmx;
        }

        /// <summary>The movement smoothness a fixture reads, which is channel 13
        /// of its own sector rather than a global constant. That is the whole
        /// claim of N12.</summary>
        static float Smoothness(int absChannel)
        {
            int ch = ((absChannel - 1) / 13 + 1) * 13;
            return VRSL_SyntheticDMXChannelSource.RampValue(ch - 1) / 255f;
        }

        static float SpinOf(VRSL_URPLightManager.VRSLLightData d) => d.spotParams.w;

        [UnityTest]
        public IEnumerator N8_spin_advances_at_the_rate_its_channel_asks_for()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 4;
                yield return rig.Step(5);
                var before = rig.ReadLights();

                const int span = 600;                       // 10 s of captured time
                yield return rig.Step(span);
                var after = rig.ReadLights();

                float elapsed = span * VRSLDMXRig.FrameDelta;
                int checkedCount = 0;
                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                {
                    int ch = VRSLDMXRig.ChannelOf(i);
                    float? rate = SpinRate(ch);
                    if (rate == null)
                    {
                        Assert.AreEqual(0f, SpinOf(after[i]), 1e-5f,
                            $"ch {ch} is below the 0.01 gate, so its phase is reset rather than held");
                        continue;
                    }

                    // Compared as a delta rather than an absolute so the row does
                    // not depend on which frame the buffer was first allocated on.
                    float moved    = Mathf.Repeat(SpinOf(after[i]) - SpinOf(before[i]), Tau);
                    float expected = Mathf.Repeat(rate.Value * elapsed, Tau);
                    float residual = Mathf.Min(Mathf.Abs(moved - expected), Tau - Mathf.Abs(moved - expected));
                    Assert.Less(residual, 0.01f,
                        $"ch {ch} advanced {moved:F4} rad in {elapsed:F2} s, expected {expected:F4} "
                      + $"at {rate.Value:F4} rad/s. All spins reading zero usually means the fixtures "
                      + "have gobo spin disabled rather than that the integrator is dead");
                    checkedCount++;
                }
                Assert.Greater(checkedCount, 40, "too few fixtures actually spin for this row to mean anything");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator N11_movement_populates_from_each_fixtures_own_pan_and_tilt()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 4;
                yield return rig.Step(VRSLDMXRig.Frames(5f));

                var dirs = rig.ReadDirections().Take(VRSLDMXRig.FixtureCount).ToArray();
                // The movement buffer is allocated zeroed and the rig configures
                // every fixture identically apart from sector, so a kernel that
                // never dispatched would leave them all sharing one direction.
                int distinct = dirs.Select(d => (Mathf.Round(d.x * 1000f), Mathf.Round(d.y * 1000f),
                                                 Mathf.Round(d.z * 1000f))).Distinct().Count();
                Assert.Greater(distinct, VRSLDMXRig.FixtureCount / 2,
                    $"only {distinct} distinct beam directions across {VRSLDMXRig.FixtureCount} "
                  + "fixtures — the movement kernel probably never ran");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator N12_damping_rate_is_sourced_per_fixture_not_globally()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                // At the shipped defaults the time constants span 0.14 s to 0.82 s
                // and everything settles long before the second sample. Both fields
                // at 0.99 stretches that to seconds, because the CRT's own per-fixture
                // pull then dominates.
                rig.Manager.movementSmoothingMax = 0.99f;
                rig.Manager.movementSmoothingMin = 0.99f;
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 4;

                yield return rig.Step(VRSLDMXRig.Frames(2f));
                var early = rig.ReadDirections();
                yield return rig.Step(VRSLDMXRig.Frames(28f));
                var late = rig.ReadDirections();

                var smooth = new double[VRSLDMXRig.FixtureCount];
                var moved  = new double[VRSLDMXRig.FixtureCount];
                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                {
                    smooth[i] = Smoothness(VRSLDMXRig.ChannelOf(i));
                    moved[i]  = Vector3.Distance(early[i], late[i]);
                }

                // Rank rather than Pearson: the relationship is exponential, so a
                // couple of fixtures moving a full radian dominate a linear measure.
                double rho = SpearmanRho(smooth, moved);
                Assert.Less(rho, -0.85,
                    $"rank correlation between a fixture's smoothness channel and how far its "
                  + $"beam travelled is {rho:F3}; a single global smoothing constant would "
                  + "leave it near zero");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>
        /// The row that separates the three readings of "damp against age".
        ///
        /// A constant age shifts a universe's clock at both ends of the
        /// subtraction, so it must change no step at all: B has to reproduce A
        /// exactly. And a universe heard every fourth frame accumulates one step
        /// of four frames rather than four of one, which is the same total, so C
        /// has to reproduce A as well — the damping and the pull are both
        /// contractions of the same error, and contractions commute.
        ///
        /// Age used as the timestep would put B roughly twelve times ahead; a step
        /// that advanced only one frame per arrival would leave C four times behind.
        /// </summary>
        [UnityTest]
        public IEnumerator N14_age_is_an_interval_not_a_timestep()
        {
            const int Settle = 900;              // 15 s, deliberately mid-convergence

            Vector3[] a = null, aHalf = null, b = null, c = null;

            yield return Run(0f, false, r => { aHalf = r; }, r => { a = r; });
            yield return Run(200f, false, null, r => { b = r; });
            yield return Run(0f, true,  null, r => { c = r; });

            // Without this the comparison is vacuous: three settled runs agree
            // whatever the step was.
            float stillMoving = Enumerable.Range(0, VRSLDMXRig.FixtureCount)
                                          .Max(i => Vector3.Distance(aHalf[i], a[i]));
            Assert.Greater(stillMoving, 0.05f,
                "every fixture had settled by the sample point, so this row would pass "
              + "under any timestep. Raise the smoothing or sample earlier");

            // Two orders of magnitude below what any of the candidate faults
            // produces — each of those leaves a fixture somewhere else entirely on
            // its sweep — and above the float noise the most sensitive fixture
            // accumulates over 900 frames of damping.
            const float Same = 0.01f;
            float worstAge = 0f, worstRotation = 0f;
            int atAge = -1, atRotation = -1;
            for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
            {
                float da = Vector3.Distance(a[i], b[i]);
                float dc = Vector3.Distance(a[i], c[i]);
                if (da > worstAge)      { worstAge = da;      atAge = i; }
                if (dc > worstRotation) { worstRotation = dc; atRotation = i; }
            }
            Assert.Less(worstAge, Same,
                $"fixture {atAge} moved {worstAge:F4} when a constant age was reported. Age is a "
              + "staleness, not a timestep — it shifts both ends of the interval and must cancel");
            Assert.Less(worstRotation, Same,
                $"fixture {atRotation} converged {worstRotation:F4} differently under rotation. One "
              + "step of four frames and four steps of one frame are the same total advance");

            IEnumerator Run(float ageMs, bool rotate, System.Action<Vector3[]> atHalf,
                            System.Action<Vector3[]> atEnd)
            {
                var rig = VRSLDMXRig.Build();
                try
                {
                    // Frames are stepped here rather than through rig.Step: this is
                    // already a level deep, and the runner drives only one.
                    for (int f = 0; f < 4; f++) { yield return null; rig.RenderFrame(); }
                    rig.Calibrate();

                    rig.Manager.movementSmoothingMax = 0.99f;
                    rig.Manager.movementSmoothingMin = 0.99f;
                    rig.Source.pattern         = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                    rig.Source.universes       = 4;
                    rig.Source.simulatedAgeMs  = ageMs;
                    rig.Source.rotateUniverses = rotate;
                    // Zero the movement buffer so all three runs start from the same
                    // state: the settings above only take effect on the next upload.
                    for (int f = 0; f < 2; f++) { yield return null; rig.RenderFrame(); }

                    for (int f = 0; f < Settle / 2; f++) { yield return null; rig.RenderFrame(); }
                    atHalf?.Invoke(rig.ReadDirections());
                    for (int f = 0; f < Settle / 2; f++) { yield return null; rig.RenderFrame(); }
                    atEnd(rig.ReadDirections());
                }
                finally { rig.Dispose(); }
            }
        }

        static double SpearmanRho(double[] x, double[] y)
        {
            double[] rx = Rank(x), ry = Rank(y);
            double mx = rx.Average(), my = ry.Average();
            double num = 0, dx = 0, dy = 0;
            for (int i = 0; i < rx.Length; i++)
            {
                num += (rx[i] - mx) * (ry[i] - my);
                dx  += (rx[i] - mx) * (rx[i] - mx);
                dy  += (ry[i] - my) * (ry[i] - my);
            }
            return num / System.Math.Sqrt(dx * dy);
        }

        static double[] Rank(double[] v)
        {
            var order = Enumerable.Range(0, v.Length).OrderBy(i => v[i]).ToArray();
            var rank = new double[v.Length];
            for (int i = 0; i < order.Length;)
            {
                int j = i;
                while (j + 1 < order.Length && v[order[j + 1]] == v[order[i]]) j++;
                double shared = (i + j) / 2.0;          // average rank across ties
                for (int k = i; k <= j; k++) rank[order[k]] = shared;
                i = j + 1;
            }
            return rank;
        }
    }
}
