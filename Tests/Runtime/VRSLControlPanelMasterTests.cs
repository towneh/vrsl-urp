#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The control panel's master slider scales every light once. Row Q11 of TESTING.md.
    /// </summary>
    class VRSLControlPanelMasterTests : VRSLDMXTest
    {
        const string Discoball = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/VRSL-DMX-Discoball-1CH.prefab";

        static Slider MakeSlider(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var slider = go.AddComponent<Slider>();
            slider.value = 1f;
            return slider;
        }

        /// <summary>
        /// Q11. The beams and the discoballs follow the manager's light, so with a light
        /// slider on the panel the master halves the light and leaves the beams' and the
        /// discoballs' own levels where they were authored. Without a light slider the master
        /// scales them directly, since nothing else carries it to them.
        /// </summary>
        [UnityTest]
        public IEnumerator Q11_the_master_slider_scales_every_light_once([Values(true, false)] bool withLightSlider)
        {
            var rig     = VRSLDMXRig.Build(fixtures: 0, withSource: false);
            var panelGo = new GameObject("Q11 panel");
            panelGo.SetActive(false);
            try
            {
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Discoball);
                Assert.IsNotNull(prefab, "the DMX discoball prefab is not in the package");
                var ball = Object.Instantiate(prefab, rig.Floor.transform.parent).GetComponent<VRSLDMXRealtimeLight>();
                Assert.AreEqual(DMXFixtureType.Discoball, ball.fixtureType, "the discoball prefab's light is not a discoball");
                rig.Manager.RefreshFixtures();
                yield return rig.Step(2);

                float authoredLight = rig.Manager.lightIntensity;
                float authoredBeams = rig.Manager.volumetricIntensity;
                float authoredBall  = ball.GlobalIntensity;
                Assert.Greater(authoredBeams, 0f, "the manager's beams are off, so nothing can show them scaled");

                var panel = panelGo.AddComponent<VRSL_LocalUIControlPanel>();
                panel.dmxManager       = rig.Manager;
                panel.masterSlider     = MakeSlider(panelGo, "Master");
                panel.fixtureSlider    = MakeSlider(panelGo, "Fixture");
                panel.volumetricSlider = MakeSlider(panelGo, "Volumetric");
                panel.discoBallSlider  = MakeSlider(panelGo, "Discoball");
                if (withLightSlider) panel.lightSlider = MakeSlider(panelGo, "Light");
                panelGo.SetActive(true);
                yield return null;   // the panel's Start

                panel.masterSlider.value = 0.5f;
                panel._SetFinalIntensity();
                yield return rig.Step(2);

                if (withLightSlider)
                {
                    Assert.AreEqual(authoredLight * 0.5f, rig.Manager.lightIntensity, 1e-4f,
                        "the master at a half must halve the light");
                    Assert.AreEqual(authoredBeams, rig.Manager.volumetricIntensity, 1e-4f,
                        "the beams follow the light, so the master must not scale them a second time");
                    Assert.AreEqual(authoredBall, ball.GlobalIntensity, 1e-4f,
                        "the discoball follows the light, so the master must not scale it a second time");
                }
                else
                {
                    Assert.AreEqual(authoredBeams * 0.5f, rig.Manager.volumetricIntensity, 1e-4f,
                        "with no light slider the master must reach the beams directly");
                    Assert.AreEqual(authoredBall * 0.5f, ball.GlobalIntensity, 1e-4f,
                        "with no light slider the master must reach the discoball directly");
                }
            }
            finally
            {
                Object.DestroyImmediate(panelGo);
                rig.Dispose();
            }
        }
    }
}
#endif
