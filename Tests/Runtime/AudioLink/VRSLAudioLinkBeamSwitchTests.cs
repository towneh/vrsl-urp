#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Row V23 of TESTING.md: the AudioLink realtime light's beam switch. V18 to V22
    /// are the DMX light's, in <see cref="VRSLBeamSwitchTests"/>.
    ///
    /// The DMX rig stands beside the AudioLink manager for its camera and its quiet
    /// host; its one DMX fixture plays no part.
    /// </summary>
    class VRSLAudioLinkBeamSwitchTests : VRSLDMXTest
    {
        const string ManagerPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-LightManager.prefab";
        const string SpotPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-Mover-Spotlight.prefab";
        const string PrefabFolder = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink";

        static Vector4 DirectionAndType(VRSL_AudioLinkURPLightManager manager)
        {
            var data = new Vector4[manager.FixtureCount * 4];
            manager.LightDataBuffer.GetData(data);
            return data[1];
        }

        static bool OutOfHaze(Vector4 directionAndType) => Mathf.Repeat(directionAndType.w, 8f) >= 4f;

        /// <summary>V23. Every shipped AudioLink realtime light has its beam on; on a
        /// spot from the prefab the switch is in the light record a frame after it is
        /// set, both ways, and leaves the type and the gobo slice alone.</summary>
        [UnityTest]
        public IEnumerator V23_AudioLink_the_beam_switch_reaches_the_light_record_the_next_frame()
        {
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder }))
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var prefab  = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
                foreach (var f in prefab.GetComponentsInChildren<VRSLAudioLinkRealtimeLight>(true))
                    Assert.IsTrue(f.Beams, $"{path}: {f.name} ships with its beam off");
            }

            VRSLDMXRig rig = null;
            GameObject root = null;
            try
            {
                rig  = VRSLDMXRig.Build(fixtures: 1, withSource: false);
                root = new GameObject("VRSL AudioLink beam switch");

                var spotPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(SpotPrefab);
                Assert.IsNotNull(spotPrefab, SpotPrefab);
                var spot = Object.Instantiate(spotPrefab, root.transform);
                spot.transform.localPosition = new Vector3(100f, 5.6f, 0f);
                var light = spot.GetComponent<VRSLAudioLinkRealtimeLight>();
                Assert.IsNotNull(light, "the AudioLink spot prefab carries no realtime light");
                light.enableAudioLink = false;
                light.goboIndex       = 3;

                var managerPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPrefab);
                Assert.IsNotNull(managerPrefab, ManagerPrefab);
                var manager = Object.Instantiate(managerPrefab, root.transform)
                                    .GetComponent<VRSL_AudioLinkURPLightManager>();
                Assert.AreSame(manager, VRSL_AudioLinkURPLightManager.Instance,
                               "the row's AudioLink manager should own the singleton");

                yield return rig.Step(3);
                Vector4 on = DirectionAndType(manager);
                Assert.IsFalse(OutOfHaze(on), $"the spot starts out of the haze: {on}");

                light.Beams = false;
                yield return rig.Step(1);
                Vector4 off = DirectionAndType(manager);
                Assert.IsTrue(OutOfHaze(off), $"the spot is still in the haze a frame after its beam went off: {off}");
                Assert.AreEqual(Mathf.Repeat(on.w, 4f), Mathf.Repeat(off.w, 4f), 1e-3f, "the spot changed type");
                Assert.AreEqual(Mathf.Floor(on.w * 0.125f), Mathf.Floor(off.w * 0.125f), 1e-3f, "the spot changed gobo");
                Assert.AreEqual(2f, Mathf.Floor(off.w * 0.125f) - 1f, 1e-3f, "Gobo Index 3 is not slice 2");

                light.Beams = true;
                yield return rig.Step(1);
                Vector4 back = DirectionAndType(manager);
                Assert.IsFalse(OutOfHaze(back), $"the spot is still out of the haze a frame after its beam came back: {back}");
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                rig?.Dispose();
            }
        }
    }
}
#endif
