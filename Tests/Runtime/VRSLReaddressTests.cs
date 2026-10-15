#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// A fixture moved to another address while the world runs, through
    /// <c>ApplyAddress()</c> and nothing else: no manager call, no inspector.
    ///
    /// Rows R1 to R3 of TESTING.md.
    /// </summary>
    class VRSLReaddressTests : VRSLDMXTest
    {
        static Vector3 Rgb(VRSL_DMXURPLightManager.VRSLLightData d)
            => new Vector3(d.colorAndIntensity.x, d.colorAndIntensity.y, d.colorAndIntensity.z);

        static int ShellChannel(Renderer r)
        {
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            return block.GetInt("_DMXChannel");
        }

        static void AssertShellsOn(VRStageLighting_DMX_RealtimeLight f, int channel, string because)
        {
            Assert.IsNotEmpty(f.fixtureShellRenderers, "the rig's mover has no body renderers to judge");
            foreach (var r in f.fixtureShellRenderers)
                Assert.AreEqual(channel, ShellChannel(r), because);
        }

        [UnityTest]
        public IEnumerator R1_a_realtime_light_moves_beam_and_body_together()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 2;
                yield return rig.Step(3);

                // One fixture through each addressing mode: sector 3 onto sector 10's
                // channels, sector 5 onto universe 2 channel 1, which is sector 40.
                var lights = rig.ReadLights();
                Assert.IsFalse(Near(Rgb(lights[3]), Rgb(lights[10])),
                    "sectors 3 and 10 must decode different colours, or matching them proves nothing");
                Assert.IsFalse(Near(Rgb(lights[5]), Rgb(lights[40])),
                    "sectors 5 and 40 must decode different colours, or matching them proves nothing");

                var bySector = rig.Fixtures[3];
                bySector.sector = 10;
                bySector.ApplyAddress();

                var byChannel = rig.Fixtures[5];
                byChannel.useLegacySectorMode = false;
                byChannel.dmxChannel  = 1;
                byChannel.dmxUniverse = 2;
                byChannel.ApplyAddress();

                yield return rig.Step(2);
                lights = rig.ReadLights();

                AssertNear(Rgb(lights[10]), Rgb(lights[3]),
                    "the beam re-addressed to sector 10 must decode sector 10's colour");
                AssertNear(Rgb(lights[40]), Rgb(lights[5]),
                    "the beam re-addressed to universe 2 channel 1 must decode sector 40's colour");
                AssertShellsOn(bySector, VRSLDMXRig.ChannelOf(10),
                    "the body must read the channel the beam moved to, or the fixture looks half-patched");
                AssertShellsOn(byChannel, 521,
                    "the body must read universe 2 channel 1, flat 521");
            }
            finally { rig.Dispose(); }
        }

        [UnityTest]
        public IEnumerator R2_a_shader_driven_fixture_takes_its_new_address()
        {
            const string Flasher = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/VRSL-DMX-Static-Flasher-1CH.prefab";
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Flasher);
            Assert.IsNotNull(prefab, $"Asset not found: {Flasher}");
            var go = Object.Instantiate(prefab);
            try
            {
                yield return null;                              // Start sets the first block
                var f = go.GetComponentInChildren<VRStageLighting_DMX_Static>();
                Assert.IsNotNull(f, "the flasher prefab has no VRStageLighting_DMX_Static");
                Assert.Greater(f.objRenderers.Length, 1, "the row wants a fixture with more than one renderer");
                foreach (var r in f.objRenderers)
                    Assert.AreEqual(1, ShellChannel(r), "the flasher ships on channel 1");

                f.dmxChannel  = 27;
                f.dmxUniverse = 2;
                f.ApplyAddress();
                foreach (var r in f.objRenderers)
                    Assert.AreEqual(27 + 520, ShellChannel(r),
                        "universe 2 channel 27 is flat 547, and every renderer must read it");

                f.useLegacySectorMode = true;
                f.singleChannelMode   = true;
                f.sector  = 3;
                f.Channel = 4;
                f.ApplyAddress();
                foreach (var r in f.objRenderers)
                    Assert.AreEqual(3 * 13 + 1 + 4, ShellChannel(r),
                        "sector 3, channel 4 of 13 is flat 44, and every renderer must read it");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [UnityTest]
        public IEnumerator R3_applying_an_unchanged_address_changes_nothing()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                // The rig patches its fixtures after they wake, so their bodies still
                // carry the prefab's address until the first call brings them in line.
                foreach (var f in rig.Fixtures) f.ApplyAddress();
                // Strobe gates intensity by the clock, which would move it between reads.
                rig.Manager.disableStrobe = true;
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 2;
                yield return rig.Step(3);

                var before = rig.ReadLights();
                foreach (var f in rig.Fixtures) f.ApplyAddress();
                yield return rig.Step(2);
                var after = rig.ReadLights();

                for (int i = 0; i < rig.Fixtures.Count; i++)
                {
                    Assert.AreEqual(before[i].colorAndIntensity, after[i].colorAndIntensity,
                        $"sector {i}'s colour and intensity moved with its address unchanged");
                    AssertShellsOn(rig.Fixtures[i], VRSLDMXRig.ChannelOf(i),
                        $"sector {i}'s body moved with its address unchanged");
                }
            }
            finally { rig.Dispose(); }
        }
    }
}
#endif
