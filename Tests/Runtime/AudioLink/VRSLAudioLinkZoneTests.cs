#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// LZ6 and LZ7 of TESTING.md: an AudioLink light zoned to the room of
    /// <see cref="VRSLZoneRoom"/> keeps its surface light, gobo and beam out of the
    /// stairway, and lights the room as it did.
    /// </summary>
    class VRSLAudioLinkZoneTests : VRSLDMXTest
    {
        const string ManagerPrefab = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-LightManager.prefab";
        const int ImageSize = 256;

        IEnumerator Row(string row, bool beams, int goboIndex, float roomTolerance)
        {
            var rig  = VRSLDMXRig.Build(fixtures: 0, withSource: false, targetSize: ImageSize);
            GameObject root = null;
            var dark   = new VRSLLightZoneTests.Views();
            var before = new VRSLLightZoneTests.Views();
            var after  = new VRSLLightZoneTests.Views();
            try
            {
                rig.Manager.surfacePropertiesShader = null;
                rig.Manager.quality = VRSLQuality.Off;
                rig.Manager.enabled = false;
                rig.Manager.enabled = true;
                var room = VRSLZoneRoom.Build(rig.Floor.transform.parent);

                // Built switched off, so the manager's first enable sees the row's settings.
                root = new GameObject("VRSL AudioLink zone row");
                root.SetActive(false);
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPrefab);
                Assert.IsNotNull(prefab, ManagerPrefab);
                var manager = Object.Instantiate(prefab, root.transform).GetComponent<VRSL_AudioLinkURPLightManager>();
                manager.surfacePropertiesShader = null;
                manager.VolumetricTimeOverride  = 0f;
                if (!beams) manager.volumetricIntensity = 1e-12f;

                var go = new GameObject("AudioLink spot");
                go.transform.SetParent(root.transform, false);
                go.transform.SetPositionAndRotation(VRSLZoneRoom.LightAt, Quaternion.Euler(0f, 90f, 0f));
                var light = go.AddComponent<VRSLAudioLinkRealtimeLight>();
                light.fixtureType     = AudioLinkFixtureType.MoverSpotlight;
                light.enableAudioLink = false;
                light.emissionColor   = Color.white;
                light.maxIntensity    = 30f;
                light.spotAngle       = 60f;
                light.range           = 20f;
                light.goboIndex       = goboIndex;
                light.goboSpinSpeed   = 0f;
                root.SetActive(true);
                Assert.AreSame(manager, VRSL_AudioLinkURPLightManager.Instance, "the row's AudioLink manager should own the singleton");

                manager.lightIntensity = 0f;
                yield return VRSLLightZoneTests.Take(rig, () => manager.volumetricIntensity,
                                                     v => manager.volumetricIntensity = v, false, dark);
                manager.lightIntensity = 1f;
                yield return VRSLLightZoneTests.Take(rig, () => manager.volumetricIntensity,
                                                     v => manager.volumetricIntensity = v, beams, before);
                light.SetZone(room.Zone);
                yield return VRSLLightZoneTests.Take(rig, () => manager.volumetricIntensity,
                                                     v => manager.volumetricIntensity = v, beams, after);

                VRSLZoneRoom.Judge(row, before.StairLight(beams, dark), after.StairLight(beams, dark),
                                   before.Room, after.Room, before.RoomLight(beams, dark), roomTolerance);
            }
            finally
            {
                dark.Destroy();
                before.Destroy();
                after.Destroy();
                if (root != null) Object.DestroyImmediate(root);
                rig.Dispose();
            }
        }

        /// <summary>LZ6. Surface light through a gobo.</summary>
        [UnityTest]
        public IEnumerator LZ6_a_zoned_AudioLink_light_keeps_its_gobo_out_of_the_stairway()
            => Row("LZ6", beams: false, goboIndex: 3, roomTolerance: 0.01f);

        /// <summary>LZ7. The beam, judged as what the haze adds.</summary>
        [UnityTest]
        public IEnumerator LZ7_a_zoned_AudioLink_light_keeps_its_beam_out_of_the_stairway()
            => Row("LZ7", beams: true, goboIndex: 1, roomTolerance: 0.02f);
    }
}
#endif
