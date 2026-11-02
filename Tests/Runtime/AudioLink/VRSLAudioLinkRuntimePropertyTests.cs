#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Row RP7 of TESTING.md: the AudioLink realtime light's properties. RP1 to RP6
    /// are the DMX fixtures', in <see cref="VRSLRuntimePropertyTests"/>.
    ///
    /// The DMX rig stands beside the AudioLink manager for its camera and its quiet
    /// host; its one DMX fixture plays no part.
    /// </summary>
    class VRSLAudioLinkRuntimePropertyTests : VRSLDMXTest
    {
        const string ManagerPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-LightManager.prefab";
        const string SpotPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-Mover-Spotlight.prefab";

        // One of the values LTCGI's adapter puts on each renderer it lights, once, at Start.
        const string ForeignProperty = "_Udon_LTCGI_ScreenCount";
        const int ForeignValue = 7;

        static VRSL_AudioLinkURPLightManager.VRSLALFixtureConfig Config(VRSL_AudioLinkURPLightManager manager)
        {
            Assert.AreEqual(1, manager.FixtureCount, "the AudioLink manager should hold the row's one spot");
            var configs = new VRSL_AudioLinkURPLightManager.VRSLALFixtureConfig[1];
            manager.FixtureConfigBuffer.GetData(configs);
            return configs[0];
        }

        static float Block(Renderer r, string name)
        {
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            return block.GetFloat(name);
        }

        /// <summary>RP7. Each property set on an AudioLink spot from the prefab is in
        /// its fixture config the next frame; the ones the body reads are on every
        /// body renderer at once, beside what another component put there.</summary>
        [UnityTest]
        public IEnumerator RP7_AudioLink_properties_reach_the_light_and_body()
        {
            VRSLDMXRig rig = null;
            GameObject root = null;
            try
            {
                rig  = VRSLDMXRig.Build(fixtures: 1, withSource: false);
                root = new GameObject("VRSL AudioLink properties");

                var spotPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(SpotPrefab);
                Assert.IsNotNull(spotPrefab, SpotPrefab);
                var spot = Object.Instantiate(spotPrefab, root.transform);
                spot.transform.localPosition = new Vector3(100f, 5.6f, 0f);
                var light = spot.GetComponent<VRSLAudioLinkRealtimeLight>();
                Assert.IsNotNull(light, "the AudioLink spot prefab carries no realtime light");
                Assert.IsNotEmpty(light.fixtureShellRenderers, "the AudioLink spot has no body renderers to judge");

                var managerPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPrefab);
                Assert.IsNotNull(managerPrefab, ManagerPrefab);
                var manager = Object.Instantiate(managerPrefab, root.transform)
                                    .GetComponent<VRSL_AudioLinkURPLightManager>();
                Assert.AreSame(manager, VRSL_AudioLinkURPLightManager.Instance,
                               "the row's AudioLink manager should own the singleton");

                yield return rig.Step(3);
                foreach (var r in light.fixtureShellRenderers)
                {
                    var block = new MaterialPropertyBlock();
                    r.GetPropertyBlock(block);
                    block.SetInteger(ForeignProperty, ForeignValue);
                    r.SetPropertyBlock(block);
                }
                var before = Config(manager);
                float authoredMax = light.maxIntensity;
                Assert.Greater(authoredMax, 0f, "the spot ships with no Max Intensity");
                Assert.AreEqual(1f, before.intensityParams.z, "the spot should start with AudioLink on");

                light.EnableAudioLink    = false;
                light.Delay              = 17;
                light.BandMultiplier     = 3.5f;
                light.FinalIntensity     = 0.5f;
                light.GlobalIntensity    = 0.4f;
                light.GoboIndex          = 4;
                light.GoboSpinSpeed      = -2.5f;
                light.MaxIntensity       = 7f;
                light.SpotAngle          = 33f;
                light.Range              = 12.5f;
                light.DiscoballSpinSpeed = -20f;

                foreach (var r in light.fixtureShellRenderers)
                {
                    Assert.AreEqual(0f,    Block(r, "_EnableAudioLink"), 1e-6f, $"{r.name}: Enable AudioLink");
                    Assert.AreEqual(17f,   Block(r, "_Delay"),           1e-6f, $"{r.name}: Delay");
                    Assert.AreEqual(3.5f,  Block(r, "_BandMultiplier"),  1e-6f, $"{r.name}: Band Multiplier");
                    Assert.AreEqual(0.5f,  Block(r, "_FinalIntensity"),  1e-6f, $"{r.name}: Final Intensity");
                    Assert.AreEqual(0.4f,  Block(r, "_GlobalIntensity"), 1e-6f, $"{r.name}: Global Intensity");
                    Assert.AreEqual(-2.5f, Block(r, "_SpinSpeed"),       1e-6f, $"{r.name}: Gobo Spin Speed");
                    var block = new MaterialPropertyBlock();
                    r.GetPropertyBlock(block);
                    Assert.AreEqual(4, block.GetInt("_ProjectionSelection"), $"{r.name}: Gobo Index");
                    Assert.IsTrue(block.HasInteger(ForeignProperty) && block.GetInteger(ForeignProperty) == ForeignValue,
                        $"{r.name} lost {ForeignProperty}: the light replaced the renderer's block instead of adding to it");
                }

                yield return rig.Step(1);
                var after = Config(manager);
                Assert.AreEqual(before.intensityParams.x * 7f / authoredMax, after.intensityParams.x, 1e-4f, "Max Intensity");
                Assert.AreEqual(0.5f * 0.4f, after.intensityParams.y, 1e-6f, "Final Intensity x Global Intensity");
                Assert.AreEqual(0f,    after.intensityParams.z, "Enable AudioLink");
                Assert.AreEqual(16.5f, after.spotAngles.y,      1e-6f, "Spot Angle (half)");
                Assert.AreEqual(12.5f, after.positionAndRange.w, 1e-6f, "Range");
                Assert.AreEqual(17f,   after.alParams.y,        1e-6f, "Delay");
                Assert.AreEqual(3.5f,  after.alParams.z,        1e-6f, "Band Multiplier");
                Assert.AreEqual(3f,    after.reserved.x,        1e-6f, "Gobo Index 4 is slot 3");
                Assert.AreEqual(-2.5f, after.reserved.y,        1e-6f, "Gobo Spin Speed");
                Assert.AreEqual(-20f,  light.discoballSpinSpeed, 1e-6f, "Discoball Spin Speed");
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
