using System.Collections;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Not a row. Prints what the rig can actually see, for when a row fails in a
    /// way that could be the rig rather than the package — an empty light data
    /// buffer looks identical whether the compute pass never ran or the manager
    /// never collected anything.
    ///
    /// Marked explicit so it never runs as part of the suite; ask for it by name.
    /// </summary>
    class VRSLDMXDiagnostics : VRSLDMXTest
    {
        [UnityTest, Explicit]
        public IEnumerator Dump_what_the_rig_sees()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 4;
                yield return rig.Step(6);

                var raw = rig.ReadRaw();
                int nonZeroColour = 0, nonZeroPos = 0;
                foreach (var d in raw)
                {
                    if (d.colorAndIntensity != Vector4.zero) nonZeroColour++;
                    if (d.positionAndRange  != Vector4.zero) nonZeroPos++;
                }

                var sb = new StringBuilder();
                sb.AppendLine("[VRSL DIAG] "
                    + $"fixtures in rig={rig.Fixtures.Count} manager={rig.Manager.FixtureCount} "
                    + $"channels={rig.Manager.ChannelCount} universes={rig.Manager.UniverseCount} "
                    + $"lightData={raw.Length} nonZeroColour={nonZeroColour} nonZeroPos={nonZeroPos}");

                for (int i = 0; i < rig.Fixtures.Count && i < 6; i++)
                {
                    int ch = VRSLDMXRig.ChannelOf(i);
                    float want = VRSL_SyntheticDMXChannelSource.RampValue(ch + 6) / 255f;
                    sb.AppendLine($"[VRSL DIAG] fixture {i} ch {ch} sector={rig.Fixtures[i].sector} "
                                + $"abs={rig.Fixtures[i].ComputeAbsoluteChannel()} wantRed={want:F4}");
                }
                for (int r = 0; r < raw.Length && r < 10; r++)
                {
                    var c = raw[r].colorAndIntensity;
                    var p = raw[r].positionAndRange;
                    sb.AppendLine($"[VRSL DIAG] raw {r} rgb=({c.x:F4},{c.y:F4},{c.z:F4}) w={c.w:F3} "
                                + $"pos=({p.x:F2},{p.y:F2},{p.z:F2})");
                }
                Debug.Log(sb.ToString());
            }
            finally { rig.Dispose(); }
        }
    }
}
