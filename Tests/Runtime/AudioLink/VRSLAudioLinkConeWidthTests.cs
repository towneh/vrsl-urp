#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Row L10 of TESTING.md: the original VRSL's AudioLink cone width as a spot angle, the
    /// conversion the migration tool uses. Read from the shipped prefabs, so a prefab whose
    /// angle drifts from the conversion's fails here.
    /// </summary>
    class VRSLAudioLinkConeWidthTests
    {
        const string Folder = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/";

        static VRSLAudioLinkRealtimeLight Light(string prefab, AudioLinkFixtureType type)
        {
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Folder + prefab);
            Assert.IsNotNull(asset, Folder + prefab);
            var light = asset.GetComponentInChildren<VRSLAudioLinkRealtimeLight>(true);
            Assert.IsNotNull(light, $"{prefab} carries no AudioLink realtime light");
            Assert.AreEqual(type, light.fixtureType, $"{prefab} is not a {type}");
            return light;
        }

        // The original's mover prefabs ship these widths, from v2.3.0 to its current main; the
        // suite can't read them, since none of its projects has the original package.
        [TestCase("VRSL-AudioLink-Mover-Spotlight.prefab", AudioLinkFixtureType.MoverSpotlight, 2.25f)]
        [TestCase("VRSL-AudioLink-Mover-Washlight.prefab", AudioLinkFixtureType.MoverWashlight, 2.7f)]
        public void L10_a_mover_takes_its_cone_from_the_original_width(string prefab, AudioLinkFixtureType type,
                                                                       float legacyDefault)
        {
            var light = Light(prefab, type);

            Assert.AreEqual(light.spotAngle, light.SpotAngleForLegacyConeWidth(legacyDefault), 1e-4f,
                "a mover left at the original prefab's width should arrive at this prefab's angle");

            // The widths the Orion mover controller animates, either side of the default.
            Assert.AreEqual(light.spotAngle * 4f / legacyDefault, light.SpotAngleForLegacyConeWidth(4f), 1e-3f,
                "a wider original cone should scale the angle by the same share");
            Assert.AreEqual(VRSLAudioLinkRealtimeLight.NarrowestLegacySpotAngle,
                light.SpotAngleForLegacyConeWidth(0.15f), 1e-4f,
                "the narrowest original cones should stop at the floor, not collapse");

            float previous = 0f;
            for (float width = 0f; width <= 5.5f + 1e-4f; width += 0.05f)
            {
                float angle = light.SpotAngleForLegacyConeWidth(width);
                Assert.GreaterOrEqual(angle, VRSLAudioLinkRealtimeLight.NarrowestLegacySpotAngle, $"width {width:F2}");
                Assert.LessOrEqual(angle, VRSLAudioLinkRealtimeLight.WidestLegacySpotAngle, $"width {width:F2}");
                Assert.GreaterOrEqual(angle, previous, $"the angle narrowed as the width grew past {width:F2}");
                previous = angle;
            }
        }

        [TestCase("VRSL-AudioLink-Static-ParLight.prefab", AudioLinkFixtureType.StaticParLight)]
        [TestCase("VRSL-AudioLink-Static-Blinder.prefab",  AudioLinkFixtureType.StaticBlinder)]
        [TestCase("VRSL-AudioLink-Discoball.prefab",       AudioLinkFixtureType.Discoball)]
        public void L10_a_fixture_the_original_width_never_reached_keeps_its_angle(string prefab,
                                                                                   AudioLinkFixtureType type)
        {
            var light = Light(prefab, type);
            foreach (float width in new[] { 0f, 0.15f, 2.5f, 5.5f })
                Assert.AreEqual(light.spotAngle, light.SpotAngleForLegacyConeWidth(width),
                    $"width {width} changed the {type}'s angle");
        }
    }
}
#endif
