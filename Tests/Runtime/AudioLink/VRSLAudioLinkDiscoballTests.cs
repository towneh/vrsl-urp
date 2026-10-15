#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Row K5 of TESTING.md: the AudioLink discoball's ball turns with its dots, and
    /// ships as a mirror. K4 and K6 are the DMX twins.
    ///
    /// The DMX rig stands beside the AudioLink manager for its camera and its quiet
    /// host; its one DMX fixture plays no part.
    /// </summary>
    class VRSLAudioLinkDiscoballTests : VRSLDMXTest
    {
        const string ManagerPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-LightManager.prefab";
        const string Prefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-Discoball.prefab";

        [UnityTest]
        public IEnumerator K5_AudioLink_the_ball_turns_with_its_dots()
        {
            VRSLDMXRig rig = null;
            GameObject root = null;
            try
            {
                rig  = VRSLDMXRig.Build(fixtures: 1, withSource: false);
                root = new GameObject("VRSL AudioLink discoball");

                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
                Assert.IsNotNull(prefab, Prefab);
                var ball = Object.Instantiate(prefab, root.transform);
                ball.transform.localPosition = new Vector3(100f, 5.6f, 0f);
                // Tilted, so a ball turned about the world's up reads as the wrong axis.
                ball.transform.localRotation = Quaternion.Euler(0f, 0f, 30f);
                var light = ball.GetComponent<VRSLAudioLinkRealtimeLight>();
                Assert.IsNotNull(light, "the AudioLink discoball prefab carries no realtime light");
                Assert.AreEqual(AudioLinkFixtureType.Discoball, light.fixtureType, "the prefab is not a discoball");
                var sphere = ball.transform.Find("Discoball-Sphere");
                Assert.IsNotNull(sphere, "the AudioLink discoball prefab has no Discoball-Sphere");
                Assert.AreSame(sphere, light.lensTransform,
                    "the AudioLink discoball prefab's lensTransform is not its sphere, so nothing turns the ball");
                VRSLDiscoballTests.AssertMirror(sphere.GetComponent<MeshRenderer>().sharedMaterial);
                light.discoballSpinSpeed = 90f;

                var managerPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPrefab);
                Assert.IsNotNull(managerPrefab, ManagerPrefab);
                var manager = Object.Instantiate(managerPrefab, root.transform)
                                    .GetComponent<VRSL_AudioLinkURPLightManager>();
                Assert.AreSame(manager, VRSL_AudioLinkURPLightManager.Instance,
                               "the row's AudioLink manager should own the singleton");

                yield return rig.Step(3);
                Quaternion first = sphere.rotation;
                Vector3 position = sphere.position;

                int frames = 12;
                yield return rig.Step(frames);

                Vector3 up     = ball.transform.up;
                float expected = 90f * VRSLDMXRig.FrameDelta * frames;
                float turned   = VRSLDiscoballTests.TurnAbout(up, first, sphere.rotation);
                Debug.Log($"[K5] ball turned {turned:F2}°, expected {expected:F2}° over {frames} frames");
                // The AudioLink compute turns the dots by the same +phase about the fixture's
                // up as the DMX one (K4 checks the sense there), so the sign is the dots'.
                Assert.AreEqual(expected, turned, expected * 0.25f,
                    "the ball did not turn at the authored rate in the dots' sense");
                Assert.Less(Quaternion.Angle(Quaternion.AngleAxis(turned, up) * first, sphere.rotation), 0.1f,
                    "the ball turned about some axis other than the fixture's up");
                Assert.Less(Vector3.Distance(position, sphere.position), 1e-4f, "turning the ball moved it");
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
