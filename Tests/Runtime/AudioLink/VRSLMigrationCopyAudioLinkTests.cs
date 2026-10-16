#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Row L12 of TESTING.md: an AudioLink fixture's settings through the migration tool's field
    /// translation, driven with a stand-in for the original VRSL's AudioLink component (its field
    /// names, and its wash mover prefab's values as literals, since no test project has the
    /// original package).
    /// </summary>
    class VRSLMigrationCopyAudioLinkTests
    {
        const string Wash = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-Mover-Washlight.prefab";

        // The original's VRStageLighting_AudioLink_Static, as its wash mover prefab ships it.
        public class UpstreamAudioLinkStatic : MonoBehaviour
        {
            public bool enableAudioLink = true;
            public int band;
            public int delay;
            public float bandMultiplier = 2.5f;
            public bool enableColorChord;
            public float globalIntensity = 1f;
            public float finalIntensity = 1f;
            public Color lightColorTint = new Color(2f, 2f, 2f, 1f);
            public bool enableColorTextureSampling;
            public Vector2 textureSamplingCoordinates = new Vector2(0.5f, 0.5f);
            public bool enableThemeColorSampling;
            public int themeColorTarget = 1;
            public float spinSpeed = -4f;
            public bool enableAutoSpin;
            public int selectGOBO = 1;
            public float coneWidth = 2.7f;
        }

        static readonly VRSLUpstreamTypes Types = new VRSLUpstreamTypes(
            typeof(UpstreamAudioLinkStatic).FullName, "none", "none", "none", "none");

        GameObject _source, _sourceAsset, _sibling;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in new[] { _source, _sourceAsset, _sibling })
                if (go != null) Object.DestroyImmediate(go);
        }

        (VRSLAudioLinkRealtimeLight ours, VRSLAudioLinkRealtimeLight light) Migrate(
            System.Action<UpstreamAudioLinkStatic> setUp = null)
        {
            var urpAsset = AssetDatabase.LoadAssetAtPath<GameObject>(Wash);
            Assert.IsNotNull(urpAsset, Wash);

            _sourceAsset = new GameObject("Upstream wash prefab");
            _sourceAsset.AddComponent<UpstreamAudioLinkStatic>();
            _source = new GameObject("Wash");
            var fixture = _source.AddComponent<UpstreamAudioLinkStatic>();
            setUp?.Invoke(fixture);

            var pair = new VRSLMigrationPair("upstream", "urp", "AudioLink Mover Washlight", VRSLMigrationKind.AudioLink);
            VRSLMigrationCopy.BeginRun();
            _sibling = VRSLMigrationCopy.PlaceSibling(_source.transform, _sourceAsset, urpAsset, pair, Types);
            Assert.IsNotNull(_sibling, "nothing was placed");

            var ours = urpAsset.GetComponentInChildren<VRSLAudioLinkRealtimeLight>(true);
            var light = _sibling.GetComponentInChildren<VRSLAudioLinkRealtimeLight>(true);
            Assert.IsNotNull(light, "the wash prefab carries no AudioLink realtime light");
            Assert.AreNotEqual(1f, ours.globalIntensity,
                "the shipped wash's intensity now matches the original's, so this row proves nothing; restate it");
            return (ours, light);
        }

        /// <summary>
        /// L12. This package tuned the AudioLink wash's intensity and band multiplier for its own
        /// lighting (0.15 and 1 against the original's 1 and 2.5). A wash nobody changed arrives at
        /// those values, as a freshly placed one has them; one that was changed keeps its share.
        /// Settings that decide what the fixture does carry as they were.
        /// </summary>
        [Test]
        public void L12_an_untouched_audiolink_wash_takes_our_calibration()
        {
            var (ours, light) = Migrate();

            Assert.AreEqual(ours.globalIntensity, light.globalIntensity, 1e-6f, "global intensity isn't ours");
            Assert.AreEqual(ours.bandMultiplier, light.bandMultiplier, 1e-6f, "band multiplier isn't ours");
            Assert.AreEqual(ours.finalIntensity, light.finalIntensity, 1e-6f, "final intensity isn't ours");
            Assert.IsTrue(light.enableAudioLink, "AudioLink didn't stay on");
            Assert.AreEqual(1, light.goboIndex, "the original's open gobo didn't carry over");
        }

        [Test]
        public void L12_a_changed_audiolink_wash_keeps_its_share_and_its_behaviour()
        {
            var (ours, light) = Migrate(f =>
            {
                f.globalIntensity = 0.5f;
                f.bandMultiplier = 5f;
                f.enableAudioLink = false;
                f.band = 2;
            });

            Assert.AreEqual(ours.globalIntensity * 0.5f, light.globalIntensity, 1e-6f,
                "half the original's intensity should arrive as half of ours");
            Assert.AreEqual(ours.bandMultiplier * 2f, light.bandMultiplier, 1e-6f,
                "twice the original's band multiplier should arrive as twice ours");
            Assert.IsFalse(light.enableAudioLink, "AudioLink switched off didn't carry over");
            Assert.AreEqual(2, (int)light.band, "the band didn't carry over");
        }
    }
}
#endif
