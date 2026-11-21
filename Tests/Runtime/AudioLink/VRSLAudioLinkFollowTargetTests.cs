#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows Y9 to Y12 of TESTING.md: an AudioLink mover's follow target saved into the
    /// Aim Constraint on its tilt transform while the world is authored. A Basis world
    /// bundle swaps every Aim Constraint for Basis's own before any script runs, keeping
    /// only the sources saved on it, so the saved source is the one a world gets.
    ///
    /// The editor does the write on the tick after OnValidate. Batch play mode never
    /// ticks, so the rows run what OnValidate queued themselves.
    /// </summary>
    class VRSLAudioLinkFollowTargetTests
    {
        const string SpotPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-Mover-Spotlight.prefab";
        const string SourceZero = "m_Sources.Array.data[0].sourceTransform";

        GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        // A spot from the prefab under a switched-off root, so its Start, which rebinds the
        // constraint at runtime, never runs: only the edit-time write can move source 0.
        VRSLAudioLinkRealtimeLight Spot(out AimConstraint aim, out Transform mark)
        {
            _root = new GameObject("VRSL follow target");
            _root.SetActive(false);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SpotPrefab);
            Assert.IsNotNull(prefab, SpotPrefab);
            var spot  = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _root.transform);
            var light = spot.GetComponent<VRSLAudioLinkRealtimeLight>();
            Assert.IsNotNull(light, "the AudioLink spot prefab carries no realtime light");
            Assert.IsNotNull(light.tiltTransform, "the AudioLink spot prefab has no tilt transform");
            aim = light.tiltTransform.GetComponent<AimConstraint>();
            Assert.IsNotNull(aim, "the AudioLink spot prefab's tilt transform has no Aim Constraint");
            Assume.That(aim.GetSource(0).sourceTransform.name, Is.EqualTo("PanTiltTarget"),
                        "the prefab's constraint should start on its own PanTiltTarget");

            mark = new GameObject("Stage mark").transform;
            mark.SetParent(_root.transform, false);
            mark.localPosition = new Vector3(3f, 0f, 4f);
            return light;
        }

        static void SetInInspector(VRSLAudioLinkRealtimeLight light, Transform target)
        {
            var so = new SerializedObject(light);
            so.FindProperty("targetToFollow").objectReferenceValue = target;
            so.ApplyModifiedProperties();
        }

        // The constraint's source 0 as a scene save writes it: a prefab instance's file
        // holds only its overrides, so a change not recorded as one is lost on save.
        static Object SavedSourceZero(VRSLAudioLinkRealtimeLight light, AimConstraint aim)
        {
            var prefabAim = PrefabUtility.GetCorrespondingObjectFromSource(aim);
            Assert.IsNotNull(prefabAim, "the spot should be a prefab instance");
            var mods = PrefabUtility.GetPropertyModifications(light.gameObject);
            if (mods == null) return null;
            foreach (var m in mods)
                if (m.target == prefabAim && m.propertyPath == SourceZero) return m.objectReference;
            return null;
        }

        /// <summary>Y9. Follow target set in the inspector, not in Play: once the editor
        /// has ticked, the Aim Constraint's source 0 is the new target.</summary>
        [Test]
        public void Y9_a_follow_target_set_in_the_inspector_reaches_the_aim_constraint()
        {
            var light = Spot(out var aim, out var mark);

            SetInInspector(light, mark);
            VRSLAudioLinkRealtimeLight.FlushPendingFollow();

            Assert.AreSame(mark, aim.GetSource(0).sourceTransform,
                           $"source 0 is '{aim.GetSource(0).sourceTransform?.name}', not the follow target");
            Assert.AreEqual(1f, aim.GetSource(0).weight, "source 0's weight");
        }

        /// <summary>Y10. The write in Y9 is recorded as the instance's override, which is
        /// what the scene file keeps, so a save carries it into the world bundle.</summary>
        [Test]
        public void Y10_the_follow_target_is_kept_when_the_scene_is_saved()
        {
            var light = Spot(out var aim, out var mark);

            SetInInspector(light, mark);
            VRSLAudioLinkRealtimeLight.FlushPendingFollow();

            Assert.AreSame(mark, SavedSourceZero(light, aim),
                           "the constraint's source 0 isn't recorded for the scene file");
        }

        /// <summary>Y11. A scene saved before the edit-time write, follow target set and
        /// the constraint still on PanTiltTarget, is put right when the editor opens it
        /// (Unity sends OnValidate to every component it loads). Clearing the follow
        /// target afterwards leaves the constraint where it is.</summary>
        [Test]
        public void Y11_a_scene_saved_before_is_put_right_when_it_opens()
        {
            var light = Spot(out var aim, out var mark);
            light.targetToFollow = mark;
            Assume.That(aim.GetSource(0).sourceTransform.name, Is.EqualTo("PanTiltTarget"));

            light.OnValidate();
            VRSLAudioLinkRealtimeLight.FlushPendingFollow();

            Assert.AreSame(mark, aim.GetSource(0).sourceTransform, "source 0 after opening");
            Assert.AreSame(mark, SavedSourceZero(light, aim), "source 0 as the scene file keeps it");

            SetInInspector(light, null);
            VRSLAudioLinkRealtimeLight.FlushPendingFollow();
            Assert.AreSame(mark, aim.GetSource(0).sourceTransform,
                           "clearing the follow target should leave the constraint alone");
        }

        /// <summary>Y12. A follow target the light can't turn towards, with no Aim
        /// Constraint on the tilt transform or no tilt transform, has a message for the
        /// inspector; a light that can follow, or follows nothing, has none.</summary>
        [Test]
        public void Y12_a_follow_target_that_cant_work_says_so()
        {
            static string Problem(VRSLAudioLinkRealtimeLight l) => l.FollowProblem;

            var light = Spot(out var aim, out var mark);
            Assert.IsNull(Problem(light), "the prefab as shipped");
            light.targetToFollow = mark;
            Assert.IsNull(Problem(light), "a follow target with an Aim Constraint to turn");
            light.targetToFollow = null;
            Assert.IsNull(Problem(light), "no follow target");

            var plain = new GameObject("No constraint");
            plain.transform.SetParent(_root.transform, false);
            var bare = plain.AddComponent<VRSLAudioLinkRealtimeLight>();
            bare.targetToFollow = mark;
            StringAssert.Contains("Aim Constraint", Problem(bare) ?? "", "no tilt transform");
            bare.tiltTransform = plain.transform;
            StringAssert.Contains("Aim Constraint", Problem(bare) ?? "", "a tilt transform with no Aim Constraint");
        }
    }
}
#endif
