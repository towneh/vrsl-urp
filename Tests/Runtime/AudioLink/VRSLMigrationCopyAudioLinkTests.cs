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
            public Transform targetToFollow;
        }

        // The original's VRStageLighting_AudioLink_RealtimeLight, as far as aiming goes.
        public class UpstreamAudioLinkRealtime : MonoBehaviour
        {
            public Transform targetToFollow;
        }

        static readonly VRSLUpstreamTypes Types = new VRSLUpstreamTypes(
            typeof(UpstreamAudioLinkStatic).FullName, "none", "none", "none", "none");

        static readonly VRSLUpstreamTypes RealtimeTypes = new VRSLUpstreamTypes(
            "none", typeof(UpstreamAudioLinkRealtime).FullName, "none", "none", "none");

        GameObject _source, _sourceAsset, _sibling, _holder;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in new[] { _source, _sourceAsset, _sibling, _holder })
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

        /// <summary>
        /// L17. A migrated AudioLink par or blinder sits where the original did and shines the same
        /// way, whatever the original's pose and scale. The original carries its body at the root,
        /// unrotated, shining down; this package's prefabs carry it turned to shine along the
        /// root's forward (the blinder's also moved), so the table's offset has to undo that.
        /// </summary>
        [TestCase("AudioLink Static ParLight", "ParLight-Projection-FixtureMesh", 1f)]
        [TestCase("AudioLink Static Blinder", "BlinderLight-AudioLink-FixtureMesh", 1.3221f)]
        public void L17_an_audiolink_par_or_blinder_lands_on_its_original(string pairName, string bodyName, float bodyScale)
        {
            var pair = VRSLMigrationPlacement.Pair(pairName);
            var urpAsset = VRSLMigrationPlacement.UrpAsset(pair);
            foreach (var pose in VRSLMigrationPlacement.SourcePoses)
            {
                TearDown();
                _sourceAsset = new GameObject("Upstream prefab");
                _sourceAsset.AddComponent<UpstreamAudioLinkStatic>();
                _source = new GameObject(pairName);
                _source.AddComponent<UpstreamAudioLinkStatic>();
                VRSLMigrationPlacement.Pose(_source.transform, pose);

                VRSLMigrationCopy.BeginRun();
                _sibling = VRSLMigrationCopy.PlaceSibling(_source.transform, _sourceAsset, urpAsset, pair, Types);
                Assert.IsNotNull(_sibling, "nothing was placed");

                string what = $"{pairName} from scale {pose.scale}";
                VRSLMigrationPlacement.AssertBodyOnOriginal(_sibling, _source.transform, bodyName, bodyScale, what);
                var light = _sibling.GetComponentInChildren<VRSLAudioLinkRealtimeLight>(true);
                Assert.IsNotNull(light, $"{what}: no AudioLink realtime light");
                VRSLMigrationPlacement.AssertShinesLikeOriginal(light.GetWorldForward(), _source.transform, what);
            }
        }

        /// <summary>
        /// L18. Running the tool again skips an original whose sibling it already placed, whether
        /// this version placed it (the blinder's root moved off the original's) or 0.3.0-rc.4 and
        /// earlier did (on the original's root). A fixture of the same prefab elsewhere is not
        /// that original's sibling.
        /// </summary>
        [Test]
        public void L18_a_rerun_recognises_a_blinder_it_placed()
        {
            var pair = VRSLMigrationPlacement.Pair("AudioLink Static Blinder");
            Assert.AreNotEqual(Vector3.zero, pair.RootShift,
                "the blinder's root no longer moves off the original's, so this row proves nothing; restate it");
            _sourceAsset = new GameObject("Upstream prefab");
            _sourceAsset.AddComponent<UpstreamAudioLinkStatic>();
            _source = new GameObject("Blinder");
            _source.AddComponent<UpstreamAudioLinkStatic>();
            VRSLMigrationPlacement.Pose(_source.transform, VRSLMigrationPlacement.SourcePoses[1]);
            VRSLMigrationCopy.BeginRun();
            _sibling = VRSLMigrationCopy.PlaceSibling(_source.transform, _sourceAsset,
                VRSLMigrationPlacement.UrpAsset(pair), pair, Types);
            const float tolerance = 0.001f;

            Assert.IsTrue(VRSLMigrationCopy.IsPlacedFor(_sibling.transform, _source.transform, pair, tolerance),
                "the blinder this tool placed isn't recognised, so a re-run adds a second one");
            var placed = _sibling.transform.position;
            _sibling.transform.position = _source.transform.position;
            Assert.IsTrue(VRSLMigrationCopy.IsPlacedFor(_sibling.transform, _source.transform, pair, tolerance),
                "a blinder placed on the original's root, as 0.3.0-rc.4 did, isn't recognised");
            _sibling.transform.position = placed + new Vector3(0f, 0.01f, 0f);
            Assert.IsFalse(VRSLMigrationCopy.IsPlacedFor(_sibling.transform, _source.transform, pair, tolerance),
                "a blinder 1 cm from where this tool places one counts as the original's sibling");
        }

        /// <summary>
        /// L19. The original's AudioLink mover aims at its own PanTiltTarget, and so does this
        /// package's. A migrated mover keeps its own target, moved to where the author put the
        /// original's, so it aims at the same spot and keeps doing so once the original is
        /// deleted. A target outside the original carries across as it was. Both from the
        /// original's AudioLink Static and from its realtime light, which the tool copies by
        /// another path.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void L19_a_migrated_mover_aims_where_the_original_did_without_it(bool fromRealtime)
        {
            var aimedAt = new Vector3(4f, -1.5f, 7f);
            var light = MigrateSpotlight(fromRealtime, source =>
            {
                var target = new GameObject("PanTiltTarget").transform;
                target.SetParent(source.transform, false);
                target.position = aimedAt;
                return target;
            });

            Assert.IsNotNull(light.targetToFollow, "the migrated mover has no target");
            Assert.IsTrue(light.targetToFollow.IsChildOf(_sibling.transform),
                $"the migrated mover aims at '{light.targetToFollow.name}' on '{light.targetToFollow.root.name}', "
                + "which goes when the original is deleted");
            Assert.Less(Vector3.Distance(aimedAt, light.targetToFollow.position), 1e-4f,
                $"the migrated mover's target is at {light.targetToFollow.position}, the original's at {aimedAt}");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void L19_a_target_outside_the_original_carries_across(bool fromRealtime)
        {
            Transform outside = null;
            var light = MigrateSpotlight(fromRealtime, source =>
            {
                _holder = new GameObject("Stage mark");
                _holder.transform.position = new Vector3(-2f, 0f, 5f);
                return outside = _holder.transform;
            });

            Assert.AreSame(outside, light.targetToFollow, "a target outside the original didn't carry across");
            Assert.AreEqual(new Vector3(-2f, 0f, 5f), outside.position, "migrating moved a target outside the original");
        }

        VRSLAudioLinkRealtimeLight MigrateSpotlight(bool fromRealtime, System.Func<GameObject, Transform> target)
        {
            var pair = VRSLMigrationPlacement.Pair("AudioLink Mover Spotlight");
            _sourceAsset = new GameObject("Upstream prefab");
            var assetTarget = new GameObject("PanTiltTarget").transform;
            assetTarget.SetParent(_sourceAsset.transform, false);
            SetTarget(_sourceAsset, fromRealtime, assetTarget);
            _source = new GameObject("Spotlight");
            _source.transform.SetPositionAndRotation(new Vector3(1f, 3f, -2f), Quaternion.Euler(10f, 40f, 0f));
            SetTarget(_source, fromRealtime, target(_source));

            VRSLMigrationCopy.BeginRun();
            _sibling = VRSLMigrationCopy.PlaceSibling(_source.transform, _sourceAsset,
                VRSLMigrationPlacement.UrpAsset(pair), pair, fromRealtime ? RealtimeTypes : Types);
            var light = _sibling.GetComponentInChildren<VRSLAudioLinkRealtimeLight>(true);
            Assert.IsNotNull(light, "the spotlight prefab carries no AudioLink realtime light");
            return light;
        }

        static void SetTarget(GameObject fixture, bool realtime, Transform target)
        {
            if (realtime) fixture.AddComponent<UpstreamAudioLinkRealtime>().targetToFollow = target;
            else fixture.AddComponent<UpstreamAudioLinkStatic>().targetToFollow = target;
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
