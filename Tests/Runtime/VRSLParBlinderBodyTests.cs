#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The par and blinder bodies, and the blinders' lens flares, follow the fixture's
    /// DMX; the 5-channel ones cast light from a 5-channel patch.
    ///
    /// Rows J1 and J3 of TESTING.md.
    /// </summary>
    class VRSLParBlinderBodyTests : VRSLDMXTest
    {
        const string Prefabs = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/";

        static readonly string[] ParsAndBlinders =
        {
            "Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-ParLight-H-13CH-URP.prefab",
            "Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-Blinder-H-13CH-URP.prefab",
            "Legacy Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-ParLight-L-13CH-URP.prefab",
            "Legacy Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-Blinder-L-13CH-URP.prefab",
            "Vertical Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-ParLight-V-13CH-URP.prefab",
            "Vertical Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-Blinder-V-13CH-URP.prefab",
            "Horizontal Mode/5-Channel Statics/DMX-5CH-URP-Fixtures/VRSL-DMX-Static-ParLight-H-5CH-URP.prefab",
            "Horizontal Mode/5-Channel Statics/DMX-5CH-URP-Fixtures/VRSL-DMX-Static-Blinder-H-5CH-URP.prefab",
            "Vertical Mode/5-Channel Statics/DMX-5CH-URP-Fixtures/VRSL-DMX-Static-ParLight-V-5CH-URP.prefab",
            "Vertical Mode/5-Channel Statics/DMX-5CH-URP-Fixtures/VRSL-DMX-Static-Blinder-V-5CH-URP.prefab",
        };

        static IEnumerable<string> FiveChannel => ParsAndBlinders.Where(p => p.Contains("-5CH-"));

        /// <summary>Upstream's <c>lightColorTint</c>, which the migration tool carries
        /// into <c>shellEmissionTint</c> for the scenes it brings across.</summary>
        static readonly Color BodyTint = new Color(2f, 2f, 2f, 1f);

        static GameObject Load(string path)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + path);
            Assert.IsNotNull(prefab, $"{path} is not in the package");
            return prefab;
        }

        /// <summary>
        /// J1. Every renderer on a par or blinder is its body or its flare, so
        /// each is listed for the realtime light to drive, and once the fixture wakes each
        /// carries its address, DMX on and the body tint.
        /// </summary>
        [Test]
        public void J1_every_par_and_blinder_drives_its_body_and_flare()
        {
            foreach (var path in ParsAndBlinders)
            {
                var prefab = Load(path);
                var light  = prefab.GetComponent<VRStageLighting_DMX_RealtimeLight>();
                Assert.IsNotNull(light, $"{path} has no realtime light on its root");
                CollectionAssert.AreEquivalent(prefab.GetComponentsInChildren<MeshRenderer>(true),
                    light.fixtureShellRenderers ?? new MeshRenderer[0],
                    $"{path}: a renderer left off the list glows at full whatever the desk sends");
                Assert.AreEqual(BodyTint, light.shellEmissionTint, $"{path}: the body tint");
                Assert.AreEqual(path.Contains("-5CH-"), light.use5ChannelMode,
                    $"{path}: the cast light must read the patch the fixture is named for");

                var go = Object.Instantiate(prefab);
                try
                {
                    var woken = go.GetComponent<VRStageLighting_DMX_RealtimeLight>();
                    foreach (var r in woken.fixtureShellRenderers)
                    {
                        Assert.IsTrue(r.HasPropertyBlock(), $"{path}: {r.name} was not driven on wake");
                        var block = new MaterialPropertyBlock();
                        r.GetPropertyBlock(block);
                        Assert.AreEqual(1, block.GetInt("_EnableDMX"), $"{path}: {r.name} must follow DMX");
                        Assert.AreEqual(woken.ComputeAbsoluteChannel(), block.GetInt("_DMXChannel"),
                            $"{path}: {r.name} must read the fixture's address");
                        Assert.AreEqual(BodyTint, block.GetColor("_Emission"), $"{path}: {r.name}'s tint");
                    }
                }
                finally { Object.DestroyImmediate(go); }
            }
        }

        /// <summary>
        /// J3. On Ramp every channel holds its own value, so the colour a 5-channel
        /// par or blinder decodes names the channels it read: +1 to +3 of its address, not
        /// the 13-channel layout's +7 to +9.
        /// </summary>
        [UnityTest]
        public IEnumerator J3_five_channel_pars_and_blinders_cast_light_from_a_five_channel_patch()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 1;

                var placed = new List<(string path, VRStageLighting_DMX_RealtimeLight light)>();
                int i = 0;
                foreach (var path in FiveChannel)
                {
                    var go = Object.Instantiate(Load(path), rig.Floor.transform.parent);
                    // Well away from the rig's own fixtures, so each record is told apart
                    // by where its light sits.
                    go.transform.localPosition = new Vector3(100f + 10f * i, 1.5f, 0f);
                    var light = go.GetComponent<VRStageLighting_DMX_RealtimeLight>();
                    light.useLegacySectorMode = true;
                    light.sector = 3 + 3 * i;
                    placed.Add((path, light));
                    i++;
                }
                rig.Manager.RefreshFixtures();
                yield return rig.Step(3);

                var raw = rig.ReadRaw();
                foreach (var (path, light) in placed)
                {
                    var at = light.transform.position;
                    var record = raw.OrderBy(d => Vector3.Distance(at,
                        new Vector3(d.positionAndRange.x, d.positionAndRange.y, d.positionAndRange.z))).First();
                    int abs = light.ComputeAbsoluteChannel();
                    var fiveChannel   = new Vector3(VRSLDMXRig.RampAt(abs + 1), VRSLDMXRig.RampAt(abs + 2),
                                                    VRSLDMXRig.RampAt(abs + 3));
                    var thirteenRead  = new Vector3(VRSLDMXRig.RampAt(VRSLDMXRig.RedChannel(abs)),
                                                    VRSLDMXRig.RampAt(VRSLDMXRig.RedChannel(abs) + 1),
                                                    VRSLDMXRig.RampAt(VRSLDMXRig.RedChannel(abs) + 2));
                    Assert.IsFalse(Near(fiveChannel, thirteenRead),
                        "the two layouts' colour channels must hold different values, or matching one proves nothing");
                    AssertNear(fiveChannel,
                        new Vector3(record.colorAndIntensity.x, record.colorAndIntensity.y, record.colorAndIntensity.z),
                        $"{path} must take its colour from +1 to +3 of its address");
                }
            }
            finally { rig.Dispose(); }
        }
    }
}
#endif
