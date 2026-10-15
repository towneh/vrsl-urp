#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows C11 and C12 of TESTING.md on the AudioLink path: the materials the AudioLink
    /// manager makes are destroyed when it is switched off, and follow its shaders and
    /// quality.
    ///
    /// The DMX rig stands beside the AudioLink manager for its camera and its quiet
    /// host; its one DMX fixture plays no part.
    /// </summary>
    class VRSLAudioLinkLifecycleTests : VRSLDMXTest
    {
        const string ManagerPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-LightManager.prefab";

        sealed class Stage : System.IDisposable
        {
            public VRSLDMXRig Rig;
            public GameObject Root;
            public VRSL_AudioLinkURPLightManager Manager;

            public void Dispose()
            {
                if (Root != null) Object.DestroyImmediate(Root);
                Rig?.Dispose();
            }
        }

        /// <summary>One AudioLink fixture, then the manager, so its enable collects the
        /// fixture and builds its materials.</summary>
        static Stage Build()
        {
            var stage = new Stage();
            try
            {
                stage.Rig  = VRSLDMXRig.Build(fixtures: 1, withSource: false);
                stage.Root = new GameObject("VRSL AudioLink lifecycle");
                var fixture = new GameObject("AudioLink fixture");
                fixture.transform.SetParent(stage.Root.transform, false);
                fixture.transform.localPosition = new Vector3(100f, 5.6f, 0f);
                fixture.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                fixture.AddComponent<VRStageLighting_AudioLink_RealtimeLight>();
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPrefab);
                Assert.IsNotNull(prefab, ManagerPrefab);
                stage.Manager = Object.Instantiate(prefab, stage.Root.transform)
                                      .GetComponent<VRSL_AudioLinkURPLightManager>();
                Assert.AreSame(stage.Manager, VRSL_AudioLinkURPLightManager.Instance,
                               "the stage's AudioLink manager should own the singleton");
                return stage;
            }
            catch
            {
                stage.Dispose();
                throw;
            }
        }

        [UnityTest]
        public IEnumerator C11_AudioLink_a_manager_switched_off_destroys_the_materials_it_made()
        {
            var s = Build();
            try
            {
                yield return s.Rig.Step(3);
                var manager    = s.Manager;
                var lighting   = manager.LightingMaterial;
                var volumetric = manager.VolumetricMaterial;
                Assert.IsTrue(lighting != null && volumetric != null,
                    "the AudioLink manager made no lighting or no volumetric material, so there "
                  + "is nothing for this row to see released");

                manager.enabled = false;
                yield return null;
                Assert.IsTrue(lighting == null,
                    "the lighting material outlived the manager that made it: it is "
                  + "HideAndDontSave, so nothing else will ever destroy it");
                Assert.IsTrue(volumetric == null,
                    "the volumetric material outlived the manager that made it");

                manager.enabled = true;
                yield return s.Rig.Step(2);
                Assert.IsTrue(manager.LightingMaterial != null && manager.VolumetricMaterial != null,
                    "switched back on, the manager has no materials to light with");
            }
            finally { s.Dispose(); }
        }

        [UnityTest]
        public IEnumerator C12_AudioLink_the_materials_follow_the_shaders_and_the_quality()
        {
            var s = Build();
            var manager = s.Manager;
            var shipped = manager.lightingShader;
            var other   = Shader.Find("Hidden/VRSL-URP/Tests/UrpReads");
            try
            {
                Assert.IsNotNull(other, "the stand-in shader is not in this project");
                yield return s.Rig.Step(3);
                Assert.IsTrue(manager.LightingMaterial != null && manager.LightingMaterial.shader == shipped,
                    "the manager isn't drawing with its own lighting shader to begin with");

                manager.enabled = false;
                manager.lightingShader = other;
                manager.enabled = true;
                yield return s.Rig.Step(1);
                Assert.IsTrue(manager.LightingMaterial != null && manager.LightingMaterial.shader == other,
                    "switched off, given another lighting shader and switched back on, the "
                  + "manager still draws with the shader it had before");

                manager.enabled = false;
                manager.lightingShader = shipped;
                manager.quality = VRSLQuality.Off;
                manager.enabled = true;
                yield return s.Rig.Step(2);
                Assert.IsTrue(manager.VolumetricMaterial == null,
                    "at quality Off, which draws no beams, the manager made a volumetric material");

                manager.quality = VRSLQuality.Standard;
                yield return s.Rig.Step(2);
                Assert.IsTrue(manager.VolumetricMaterial != null,
                    "switched from Off to Standard while running, the manager has no volumetric "
                  + "material, so no beams appear");
            }
            finally
            {
                if (manager != null) manager.lightingShader = shipped;
                s.Dispose();
            }
        }
    }
}
#endif
