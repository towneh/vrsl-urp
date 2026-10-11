#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Replace Superseded Prefabs in Scene: an instance of an earlier release's per-layout
    /// prefab comes out as an instance of the single prefab, with what was set on it.
    /// </summary>
    public class VRSLSupersededPrefabTests
    {
        const string Dmx = VRSLWiring.PackageRoot + "Runtime/Prefabs/DMX/";

        static GameObject Load(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(asset, $"prefab not found: {path}");
            return asset;
        }

        static string SourcePath(GameObject instance) =>
            AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(instance));

        [Test]
        public void SupersededInstancesMoveOntoTheSinglePrefabsKeepingTheirSettings()
        {
            // Under an inactive parent nothing wakes, so the manager claims nothing.
            var holder = new GameObject("VRSL superseded prefab test");
            holder.SetActive(false);
            try
            {
                var spot = (GameObject)PrefabUtility.InstantiatePrefab(
                    Load(Dmx + "Superseded/VRSL-DMX-Mover-Spotlight-V-13CH-URP.prefab"), holder.transform);
                spot.name = "Stage left spot";
                spot.transform.SetPositionAndRotation(new Vector3(3f, 4f, 5f), Quaternion.Euler(10f, 20f, 30f));
                var light = spot.GetComponentInChildren<VRStageLighting_DMX_RealtimeLight>(true);
                light.sector = 17;
                light.globalIntensity = 0.4f;
                PrefabUtility.RecordPrefabInstancePropertyModifications(light);

                var flasher = (GameObject)PrefabUtility.InstantiatePrefab(
                    Load(Dmx + "Superseded/VRSL-DMX-Static-Flasher-V-1CH.prefab"), holder.transform);

                var manager = (GameObject)PrefabUtility.InstantiatePrefab(
                    Load(Dmx + "Superseded/VRSL-DMX-URP-LightManager-Vertical.prefab"), holder.transform);
                Assert.AreEqual(VRSLDmxInput.VerticalGrid, manager.GetComponent<VRSL_URPLightManager>().dmxInput,
                    "the Vertical manager prefab should come up on the Vertical grid");

                var current = (GameObject)PrefabUtility.InstantiatePrefab(
                    Load(Dmx + "VRSL-DMX-Mover-WashLight-13CH.prefab"), holder.transform);

                var result = VRSLSupersededPrefabs.ReplaceIn(SceneManager.GetActiveScene());
                Assert.AreEqual(3, result.Replaced, "the three superseded instances, and nothing else");

                Assert.AreEqual(Dmx + "VRSL-DMX-Mover-Spotlight-13CH.prefab", SourcePath(spot));
                Assert.AreEqual("Stage left spot", spot.name, "a name the author gave stays");
                Assert.That(Vector3.Distance(spot.transform.position, new Vector3(3f, 4f, 5f)), Is.LessThan(1e-4f));
                Assert.That(Quaternion.Angle(spot.transform.rotation, Quaternion.Euler(10f, 20f, 30f)), Is.LessThan(0.01f));
                light = spot.GetComponentInChildren<VRStageLighting_DMX_RealtimeLight>(true);
                Assert.AreEqual(17, light.sector, "the address set on the instance stays");
                Assert.AreEqual(0.4f, light.globalIntensity, 1e-6f);

                Assert.AreEqual(Dmx + "VRSL-DMX-Static-Flasher-1CH.prefab", SourcePath(flasher));
                Assert.AreEqual("VRSL-DMX-Static-Flasher-1CH", flasher.name, "an untouched name follows the prefab");

                Assert.AreEqual(Dmx + "VRSL-DMX-LightManager.prefab", SourcePath(manager));
                Assert.AreEqual(VRSLDmxInput.VerticalGrid, manager.GetComponent<VRSL_URPLightManager>().dmxInput,
                    "a Vertical manager keeps reading the Vertical grid");

                Assert.AreEqual(Dmx + "VRSL-DMX-Mover-WashLight-13CH.prefab", SourcePath(current));
                Assert.AreEqual(0, VRSLSupersededPrefabs.ReplaceIn(SceneManager.GetActiveScene()).Replaced,
                    "a second run finds nothing");
            }
            finally
            {
                Object.DestroyImmediate(holder);
            }
        }
    }
}
#endif
