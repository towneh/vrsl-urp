#if UNITY_EDITOR
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows L11, L13 to L16 of TESTING.md: the migration tool's sibling placement, field
    /// translation and reference handling, driven with stand-ins for the original VRSL's DMX component (its field names,
    /// and its wash mover prefab's values as literals, since no test project has the original
    /// package). The tool's scene walk is L4/L5's.
    /// </summary>
    public class VRSLMigrationCopyTests
    {
        const string Wash = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/VRSL-DMX-Mover-WashLight-13CH.prefab";

        // The original's VRStageLighting_DMX_Static, as its H 13CH wash mover prefab ships it.
        public class UpstreamDmxStatic : MonoBehaviour
        {
            public bool enableDMXChannels = true;
            public bool enableFineChannels;
            public int dmxChannel = 1;
            public int dmxUniverse = 1;
            public bool useLegacySectorMode;
            public int sector;
            public float globalIntensity = 1f;
            public float finalIntensity = 1f;
            public Color lightColorTint = new Color(2f, 2f, 2f, 2f);
            public bool invertPan = true;
            public bool invertTilt = true;
            public bool enableAutoSpin = true;
            public bool enableStrobe = true;
            public float tiltOffsetBlue = 90f;
            public float panOffsetBlueGreen;
            public float maxMinPan = 180f;
            public float maxMinTilt = -180f;
            public bool legacyGoboRange;
            public float coneLength = 8.5f;
            public bool foldout;

            public void ApplyAddress() { }
            public void ApplyAddress(int universe) { }
            public void UpstreamOnly() { }
        }

        // A world script holding references into a fixture.
        public class RefHolder : MonoBehaviour
        {
            public Behaviour anyFixture;
            public UpstreamDmxStatic typedFixture;
            public GameObject anyObject;
            public Transform anyTransform;
            public UnityEvent onPress = new UnityEvent();
            public UnityEvent<bool> onToggle = new UnityEvent<bool>();
            public UnityEvent<int> onPick = new UnityEvent<int>();
        }

        static readonly VRSLUpstreamTypes Types = new VRSLUpstreamTypes(
            "none", "none", "none", typeof(UpstreamDmxStatic).FullName, "none");

        GameObject _source, _sourceAsset, _sibling, _holder;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in new[] { _source, _sourceAsset, _sibling, _holder })
                if (go != null) Object.DestroyImmediate(go);
        }

        GameObject Place(System.Action<GameObject, UpstreamDmxStatic> setUp = null)
        {
            var urpAsset = AssetDatabase.LoadAssetAtPath<GameObject>(Wash);
            Assert.IsNotNull(urpAsset, Wash);

            _sourceAsset = new GameObject("Upstream wash prefab");
            _sourceAsset.AddComponent<UpstreamDmxStatic>();
            _source = new GameObject("Wash");
            var fixture = _source.AddComponent<UpstreamDmxStatic>();
            setUp?.Invoke(_source, fixture);

            var pair = new VRSLMigrationPair("upstream", "urp", "DMX H 13CH Mover Washlight", VRSLMigrationKind.Dmx);
            VRSLMigrationCopy.BeginRun();
            _sibling = VRSLMigrationCopy.PlaceSibling(_source.transform, _sourceAsset, urpAsset, pair, Types);
            Assert.IsNotNull(_sibling, "nothing was placed");
            return urpAsset;
        }

        /// <summary>
        /// L11. A sibling comes out as its original stands: switched off when the original is,
        /// on the original's layer and tag, and with its light switched off when the original's
        /// fixture component is. Otherwise a fixture the world kept dark (a spare, a room switched
        /// off at start) lights the scene once migrated, and cameras that leave a layer out see it.
        /// </summary>
        [Test]
        public void L11_a_sibling_takes_its_originals_state()
        {
            const int layer = 2;      // Ignore Raycast, built in
            const string tag = "Finish";
            Place((go, fixture) =>
            {
                go.layer = layer;
                go.tag = tag;
                fixture.enabled = false;
                go.SetActive(false);
            });

            Assert.IsFalse(_sibling.activeSelf, "the original is switched off, the sibling isn't");
            Assert.AreEqual(tag, _sibling.tag, "the sibling didn't take the original's tag");
            foreach (var t in _sibling.GetComponentsInChildren<Transform>(true))
                Assert.AreEqual(layer, t.gameObject.layer, $"'{t.name}' isn't on the original's layer");
            var light = _sibling.GetComponentInChildren<VRSLDMXRealtimeLight>(true);
            Assert.IsNotNull(light, "the wash prefab carries no DMX realtime light");
            Assert.IsFalse(light.enabled, "the original's fixture component is off, the sibling's light isn't");
        }

        /// <summary>
        /// L17. A migrated DMX par or blinder sits where the original did and shines the same way,
        /// whatever the original's pose and scale. The DMX prefabs carry their body as the
        /// original's do, so these place with no offset; the AudioLink pair is the case the offset
        /// is for.
        /// </summary>
        [TestCase("DMX H 13CH Static ParLight", "ParLight-Projection-FixtureMesh", 1f)]
        [TestCase("DMX H 13CH Static Blinder", "BlinderLight-Projection-FixtureMesh", 1.3221f)]
        [TestCase("DMX H 5CH Static ParLight", "ParLight-Projection-FixtureMesh", 1f)]
        [TestCase("DMX H 5CH Static Blinder", "BlinderLight-Projection-FixtureMesh", 1.3221f)]
        public void L17_a_dmx_par_or_blinder_lands_on_its_original(string pairName, string bodyName, float bodyScale)
        {
            var pair = VRSLMigrationPlacement.Pair(pairName);
            var urpAsset = VRSLMigrationPlacement.UrpAsset(pair);
            foreach (var pose in VRSLMigrationPlacement.SourcePoses)
            {
                TearDown();
                _sourceAsset = new GameObject("Upstream prefab");
                _sourceAsset.AddComponent<UpstreamDmxStatic>();
                _source = new GameObject(pairName);
                _source.AddComponent<UpstreamDmxStatic>();
                VRSLMigrationPlacement.Pose(_source.transform, pose);

                VRSLMigrationCopy.BeginRun();
                _sibling = VRSLMigrationCopy.PlaceSibling(_source.transform, _sourceAsset, urpAsset, pair, Types);
                Assert.IsNotNull(_sibling, "nothing was placed");

                string what = $"{pairName} from scale {pose.scale}";
                VRSLMigrationPlacement.AssertBodyOnOriginal(_sibling, _source.transform, bodyName, bodyScale, what);
                var light = _sibling.GetComponentInChildren<VRSLDMXRealtimeLight>(true);
                Assert.IsNotNull(light, $"{what}: no DMX realtime light");
                VRSLMigrationPlacement.AssertShinesLikeOriginal(
                    light.transform.TransformDirection(light.localLightDirection.normalized), _source.transform, what);
            }
        }

        /// <summary>
        /// L13. Settings that decide what a show does carry over as the original had them, even
        /// where this package's prefab differs: the original's wash pans inverted, ours doesn't,
        /// and a show programmed on the original expects the inverted pan. Its intensities, which
        /// are calibration, arrive at this package's values.
        /// </summary>
        [Test]
        public void L13_an_untouched_original_keeps_its_behaviour_and_takes_our_calibration()
        {
            var urpAsset = Place();
            var ours = urpAsset.GetComponentInChildren<VRSLDMXRealtimeLight>(true);
            var light = _sibling.GetComponentInChildren<VRSLDMXRealtimeLight>(true);

            Assert.IsFalse(ours.invertPan, "the shipped wash has started inverting pan; restate this row");
            Assert.IsTrue(light.invertPan, "the original's inverted pan didn't carry over");
            Assert.IsTrue(light.invertTilt, "the original's inverted tilt didn't carry over");
            Assert.AreEqual(-180f, light.maxMinTilt, "the original's tilt range didn't carry over");
            Assert.AreEqual(ours.globalIntensity, light.globalIntensity, 1e-6f, "global intensity isn't ours");
            Assert.AreEqual(ours.finalIntensity, light.finalIntensity, 1e-6f, "final intensity isn't ours");
            // Colour only: the managers and the body shaders don't read the tint's alpha.
            var tint = light.shellEmissionTint;
            var ourTint = ours.shellEmissionTint;
            Assert.AreEqual(new Vector3(ourTint.r, ourTint.g, ourTint.b), new Vector3(tint.r, tint.g, tint.b),
                "the tint isn't ours");
        }

        /// <summary>
        /// L14. A setting the author changed on the original that the URP fixture has no place for
        /// is named, so it doesn't vanish without a word; one left at the original prefab's value
        /// isn't, and nor is the inspector's fold state.
        /// </summary>
        [Test]
        public void L14_a_changed_setting_with_no_place_is_named()
        {
            LogAssert.Expect(LogType.Warning,
                new Regex(@"'Wash' has settings the URP fixture has no place for.*Cone Length \(8\.5 on the original prefab, 55 here\)"));
            Place((go, fixture) =>
            {
                fixture.coneLength = 55f;
                fixture.foldout = true;
            });

            CollectionAssert.AreEquivalent(new[] { "coneLength" }, VRSLMigrationCopy.NotCarried.Keys,
                "the changed settings named aren't just the cone length");
            Assert.AreEqual(1, VRSLMigrationCopy.NotCarried["coneLength"]);
        }

        [Test]
        public void L14_an_untouched_original_names_nothing()
        {
            Place();
            Assert.IsEmpty(VRSLMigrationCopy.NotCarried, "settings named on a fixture nobody changed");
        }

        /// <summary>
        /// L15. When the in-place pass replaces an original component with the URP one, references to
        /// it follow where they can: a field that takes any component, and a call the URP component
        /// can take, by method name and arguments, whether the call carries its argument or the event
        /// supplies it. A field typed to the original's class, a call to a method ours lacks, and calls
        /// to a method ours has under the same name with other arguments are handed back, untouched,
        /// for the tool to name.
        /// </summary>
        [Test]
        public void L15_references_to_a_replaced_component_follow_it_where_they_can()
        {
            _source = new GameObject("Custom fixture");
            _source.SetActive(false);   // nothing of ours wakes up on a bare object
            var original = _source.AddComponent<UpstreamDmxStatic>();
            var urp = _source.AddComponent<VRSLDMXStatic>();

            _holder = new GameObject("Lights button");
            var holder = _holder.AddComponent<RefHolder>();
            holder.anyFixture = original;
            holder.typedFixture = original;
            UnityEventTools.AddVoidPersistentListener(holder.onPress, original.ApplyAddress);
            UnityEventTools.AddVoidPersistentListener(holder.onPress, original.UpstreamOnly);
            var setEnabled = (UnityAction<bool>)System.Delegate.CreateDelegate(typeof(UnityAction<bool>), original, "set_enabled");
            UnityEventTools.AddBoolPersistentListener(holder.onPress, setEnabled, false);
            UnityEventTools.AddIntPersistentListener(holder.onPress, original.ApplyAddress, 3);
            UnityEventTools.AddPersistentListener(holder.onToggle, setEnabled);
            UnityEventTools.AddPersistentListener(holder.onPick, (UnityAction<int>)original.ApplyAddress);

            var index = new VRSLMigrationCopy.ReferenceIndex(SceneManager.GetActiveScene());
            var left = VRSLMigrationCopy.Repoint(original, urp, index, out int moved);

            Assert.AreSame(urp, holder.anyFixture, "a field that takes any component didn't follow");
            Assert.AreSame(urp, holder.onPress.GetPersistentTarget(0), "the call to ApplyAddress didn't follow");
            Assert.AreEqual("ApplyAddress", holder.onPress.GetPersistentMethodName(0));
            Assert.AreSame(original, holder.typedFixture, "a field typed to the original's class was changed");
            Assert.AreSame(original, holder.onPress.GetPersistentTarget(1), "a call ours can't take was changed");
            Assert.AreSame(urp, holder.onPress.GetPersistentTarget(2), "a call carrying a bool to set_enabled didn't follow");
            Assert.AreSame(original, holder.onPress.GetPersistentTarget(3),
                "a call carrying an int to ApplyAddress was moved, though ours takes no argument");
            Assert.AreSame(urp, holder.onToggle.GetPersistentTarget(0), "a bool event's call to set_enabled didn't follow");
            Assert.AreSame(original, holder.onPick.GetPersistentTarget(0),
                "an int event's call to ApplyAddress was moved, though ours takes no argument");
            Assert.AreEqual(4, moved);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "typedFixture",
                    "onPress.m_PersistentCalls.m_Calls.Array.data[1].m_Target",
                    "onPress.m_PersistentCalls.m_Calls.Array.data[3].m_Target",
                    "onPick.m_PersistentCalls.m_Calls.Array.data[0].m_Target",
                },
                left.Select(l => l.path), "the references handed back aren't the four that can't follow");
        }

        /// <summary>
        /// L16. Before the author deletes an original, the sibling pass can say what goes with it:
        /// references from elsewhere into the original, each with what it should point at on the URP
        /// fixture (the original's fixture component maps to the URP fixture's, though their types
        /// differ), and what the author added under it. A reference from inside the original goes with
        /// it and isn't listed.
        /// </summary>
        [Test]
        public void L16_deleting_an_original_lists_the_references_into_it()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(Wash);
            Assert.IsNotNull(asset, Wash);
            _sibling = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            var siblingChild = UniqueChild(_sibling);

            // The original as the original package builds it: its own fixture component, a child of
            // the same name as one of ours.
            _source = new GameObject("Wash");
            var fixture = _source.AddComponent<UpstreamDmxStatic>();
            var child = new GameObject(siblingChild.name).transform;
            child.SetParent(_source.transform, false);
            child.gameObject.AddComponent<RefHolder>().anyObject = _source;   // inside the original

            _holder = new GameObject("Lights button");
            var holder = _holder.AddComponent<RefHolder>();
            holder.anyObject = _source;
            holder.anyTransform = child;
            holder.anyFixture = fixture;

            var index = new VRSLMigrationCopy.ReferenceIndex(SceneManager.GetActiveScene());
            var losses = VRSLMigrationCopy.WhatDeletingTakes(_source, _sibling,
                VRSLMigrationCopy.OriginalFixture(_source, Types), VRSLMigrationCopy.SiblingFixture(_sibling), index);

            var references = losses.Where(l => l.Path != null).ToDictionary(l => l.Path, l => l.Counterpart);
            CollectionAssert.AreEquivalent(new[] { "anyObject", "anyTransform", "anyFixture" }, references.Keys,
                "the references listed aren't the outside holder's three");
            Assert.AreSame(_sibling, references["anyObject"], "the root's counterpart isn't the URP fixture");
            Assert.AreSame(siblingChild, references["anyTransform"], "the child's counterpart isn't the same-named one");
            Assert.AreSame(_sibling.GetComponent<VRSLDMXRealtimeLight>(), references["anyFixture"],
                "the original's fixture component doesn't map to the URP fixture's");
        }

        [Test]
        public void L16_deleting_an_original_lists_what_was_added_to_it()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(Wash);
            Assert.IsNotNull(asset, Wash);
            _source = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            _sibling = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            var child = UniqueChild(_source);
            var added = new GameObject("Haze machine");
            added.transform.SetParent(child, false);

            var index = new VRSLMigrationCopy.ReferenceIndex(SceneManager.GetActiveScene());
            var losses = VRSLMigrationCopy.WhatDeletingTakes(_source, _sibling,
                _source.GetComponent<VRSLDMXRealtimeLight>(), _sibling.GetComponent<VRSLDMXRealtimeLight>(), index);

            var addedLosses = losses.Where(l => l.Path == null).ToList();
            Assert.AreEqual(1, addedLosses.Count, "the added object isn't listed once");
            Assert.AreSame(added, addedLosses[0].Thing);
            Assert.AreSame(UniqueChild(_sibling), addedLosses[0].Counterpart,
                "the added object's place on the URP fixture isn't its parent's counterpart");
        }

        // The first object under a fixture whose name nothing else in it shares.
        static Transform UniqueChild(GameObject root) =>
            root.GetComponentsInChildren<Transform>(true)
                .Where(t => t != root.transform)
                .GroupBy(t => t.name).Where(g => g.Count() == 1).Select(g => g.Single()).First();
    }
}
#endif
