#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Row GW10 of TESTING.md: with both light managers running beams over the same
    /// gobos, the scene holds one gobo wheel array and one beam noise volume. Counted
    /// by name among the live render textures, so the count is of what exists rather
    /// than of what the code says it built.
    /// </summary>
    class VRSLSceneResourceCountTests : VRSLDMXTest
    {
        const string ManagerPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-LightManager.prefab";

        static int Live(string name) =>
            Resources.FindObjectsOfTypeAll<RenderTexture>().Count(rt => rt != null && rt.name == name);

        [UnityTest]
        public IEnumerator GW10_both_managers_share_one_gobo_wheel_and_one_noise_volume()
        {
            int wheelsBefore = Live("_VRSLGobos");
            int noiseBefore  = Live("VRSL Volumetric Noise");
            VRSLDMXRig rig = null;
            GameObject root = null;
            try
            {
                rig = VRSLDMXRig.Build(fixtures: 1);
                rig.Manager.quality = VRSLQuality.Standard;

                root = new GameObject("VRSL scene resource count");
                root.SetActive(false);
                var light = new GameObject("AudioLink spot");
                light.transform.SetParent(root.transform, false);
                light.transform.SetPositionAndRotation(new Vector3(10f, 4.5f, 4f), Quaternion.Euler(90f, 0f, 0f));
                var spot = light.AddComponent<VRSLAudioLinkRealtimeLight>();
                spot.enableAudioLink = false;
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPrefab);
                Assert.IsNotNull(prefab, ManagerPrefab);
                var al = Object.Instantiate(prefab, root.transform).GetComponent<VRSL_AudioLinkURPLightManager>();
                al.quality = VRSLQuality.Standard;
                root.SetActive(true);

                yield return rig.Step(4);

                Assert.IsTrue(rig.Manager.VolumetricsEnabled && al.VolumetricsEnabled,
                              "the row needs beams on both managers");
                int wheels = Live("_VRSLGobos") - wheelsBefore;
                int noise  = Live("VRSL Volumetric Noise") - noiseBefore;
                Debug.Log($"[GW10] {wheels} gobo wheel array(s) and {noise} noise volume(s) with both managers running");
                Assert.AreEqual(1, wheels, "the scene holds more than one gobo wheel array");
                Assert.AreEqual(1, noise, "the scene baked more than one beam noise volume");
                Assert.AreSame(rig.Manager.GoboArray, al.GoboArray, "the managers report different wheels");
                Assert.AreSame(rig.Manager.VolumetricNoiseTexture, al.VolumetricNoiseTexture,
                               "the managers report different noise volumes");
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                rig?.Dispose();
            }
            // A render texture released in Play mode goes at the end of the frame.
            yield return null;
            Assert.AreEqual(wheelsBefore, Live("_VRSLGobos"), "a gobo wheel array outlived both managers");
            Assert.AreEqual(noiseBefore, Live("VRSL Volumetric Noise"), "a noise volume outlived both managers");
        }
    }
}
#endif
