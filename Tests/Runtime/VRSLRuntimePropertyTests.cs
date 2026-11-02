#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Settings a world changes while it runs, through the lights' properties: the
    /// DMX realtime light's intensities, the alternate address on the DMX realtime
    /// light and DMX Static, and a uGUI control's persistent listener calling them.
    ///
    /// Rows RP1 to RP6 and RP8 of TESTING.md; RP7 is the AudioLink light's, in the
    /// AudioLink test assembly.
    /// </summary>
    class VRSLRuntimePropertyTests : VRSLDMXTest
    {
        const string SpotPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/VRSL-DMX-Mover-Spotlight-13CH.prefab";
        const string FlasherPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/VRSL-DMX-Static-Flasher-1CH.prefab";

        static Vector3 Rgb(VRSL_DMXURPLightManager.VRSLLightData d)
            => new Vector3(d.colorAndIntensity.x, d.colorAndIntensity.y, d.colorAndIntensity.z);

        static float Block(Renderer r, string name)
        {
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            return block.GetFloat(name);
        }

        static int BlockInt(Renderer r, string name)
        {
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            return block.GetInt(name);
        }

        static void AssertShells(VRSLDMXRealtimeLight f, string name, float expected, string because)
        {
            Assert.IsNotEmpty(f.fixtureShellRenderers, "the rig's mover has no body renderers to judge");
            foreach (var r in f.fixtureShellRenderers)
                Assert.AreEqual(expected, Block(r, name), 1e-6f, because);
        }

        static void AssertShellsInt(VRSLDMXRealtimeLight f, string name, int expected, string because)
        {
            Assert.IsNotEmpty(f.fixtureShellRenderers, "the rig's mover has no body renderers to judge");
            foreach (var r in f.fixtureShellRenderers)
                Assert.AreEqual(expected, BlockInt(r, name), because);
        }

        // The decoded intensity comes back through a half, so it agrees to about a
        // part in a thousand.
        static void AssertRatio(float expected, float before, float after, string because)
            => Assert.AreEqual(expected, after / before, 2e-3f, because);

        /// <summary>The fixture config the manager uploaded for a light, found by the
        /// light's position: the rig spaces its fixtures along x, and the config order
        /// is the manager's own.</summary>
        static VRSL_DMXURPLightManager.VRSLFixtureConfig ConfigOf(VRSLDMXRig rig, VRSLDMXRealtimeLight f)
        {
            var configs = new VRSL_DMXURPLightManager.VRSLFixtureConfig[rig.Manager.FixtureCount];
            rig.Manager.FixtureConfigBuffer.GetData(configs);
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < configs.Length; i++)
            {
                float d = Mathf.Abs(configs[i].positionAndRange.x - f.transform.position.x);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            Assert.Less(bestDistance, 0.25f, $"no uploaded config sits at {f.name}");
            return configs[best];
        }

        static IEnumerator RampRig(VRSLDMXRig rig, int universes = 1)
        {
            // Strobe gates intensity by the clock, which would move it between reads.
            rig.Manager.disableStrobe = true;
            yield return rig.Step(4);
            rig.Calibrate();
            rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
            rig.Source.universes = universes;
            yield return rig.Step(3);
        }

        /// <summary>RP1. A DMX realtime light's intensities set while the world runs
        /// reach its beam and body with nothing else called.</summary>
        [UnityTest]
        public IEnumerator RP1_DMX_intensity_set_at_runtime_reaches_the_light_and_body()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return RampRig(rig);
                var f = rig.Fixtures[3];
                var before = rig.ReadLights();
                Assert.Greater(before[3].colorAndIntensity.w, 0f, "sector 3 must be lit, or a ratio proves nothing");
                Assert.Greater(before[4].colorAndIntensity.w, 0f, "sector 4 must be lit, or a ratio proves nothing");

                f.GlobalIntensity = 0.25f;
                yield return rig.Step(2);
                var after = rig.ReadLights();
                AssertRatio(0.25f, before[3].colorAndIntensity.w, after[3].colorAndIntensity.w,
                    "Global Intensity 0.25 set at runtime didn't reach the light");
                AssertRatio(1f, before[4].colorAndIntensity.w, after[4].colorAndIntensity.w,
                    "the neighbouring light changed");
                AssertShells(f, "_GlobalIntensity", 0.25f, "the body didn't take Global Intensity 0.25");

                f.FinalIntensity = 0.5f;
                yield return rig.Step(2);
                after = rig.ReadLights();
                AssertRatio(0.125f, before[3].colorAndIntensity.w, after[3].colorAndIntensity.w,
                    "Final Intensity 0.5 set at runtime didn't reach the light");
                AssertShells(f, "_FinalIntensity", 0.5f, "the body didn't take Final Intensity 0.5");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>RP8. A DMX realtime light's maximum intensity, range and spot angles
        /// set while the world runs reach its uploaded config, and its beam, with nothing
        /// else called.</summary>
        [UnityTest]
        public IEnumerator RP8_DMX_range_and_cone_set_at_runtime_reach_the_light()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                var f = rig.Fixtures[3];
                Assert.IsTrue(f.enableConeWidth, "the rig's mover should zoom, so both spot angles count");
                yield return RampRig(rig);
                var before       = rig.ReadLights();
                var configBefore = ConfigOf(rig, f);
                Assert.Greater(before[3].colorAndIntensity.w, 0f, "sector 3 must be lit, or a ratio proves nothing");
                Assert.AreNotEqual(12.5f, configBefore.positionAndRange.w, "the rig's range already reads 12.5");

                f.MaxIntensity = f.maxIntensity * 0.5f;
                f.Range        = 12.5f;
                f.MinSpotAngle = 8f;
                f.MaxSpotAngle = 40f;
                yield return rig.Step(2);

                var config = ConfigOf(rig, f);
                Assert.AreEqual(configBefore.rightAndMaxIntensity.w * 0.5f, config.rightAndMaxIntensity.w, 1e-4f,
                    "Max Intensity set at runtime didn't reach the uploaded config");
                Assert.AreEqual(12.5f, config.positionAndRange.w, 1e-6f, "Range set at runtime didn't reach the uploaded config");
                Assert.AreEqual(20f, config.spotAngles.y, 1e-6f, "Max Spot Angle 40 is a half angle of 20 in the uploaded config");
                Assert.AreEqual(4f,  config.spotAngles.w, 1e-6f, "Min Spot Angle 8 is a half angle of 4 in the uploaded config");
                AssertRatio(0.5f, before[3].colorAndIntensity.w, rig.ReadLights()[3].colorAndIntensity.w,
                    "half the Max Intensity didn't halve the light");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>RP2. A setter given the value the light already has raises no
        /// config change, so a relay firing every frame with an unchanged value
        /// doesn't re-upload every fixture.</summary>
        [Test]
        public void RP2_a_setter_raises_a_config_change_only_when_the_value_changes()
        {
            var go = new GameObject("VRSL RP2 light");
            int raised = 0;
            void Count() => raised++;
            try
            {
                var f = go.AddComponent<VRSLDMXRealtimeLight>();
                VRSLDMXRealtimeLight.ConfigChanged += Count;

                f.GlobalIntensity = f.GlobalIntensity;
                f.FinalIntensity  = f.FinalIntensity;
                f.UseAlternateAddress = f.UseAlternateAddress;
                Assert.AreEqual(0, raised, "a setter given the current value raised a config change");

                f.GlobalIntensity = 0.5f;
                Assert.AreEqual(1, raised, "Global Intensity changed without one config change");
                f.GlobalIntensity = 0.5f;
                Assert.AreEqual(1, raised, "Global Intensity set again to the same value raised another");

                f.FinalIntensity = 0.5f;
                Assert.AreEqual(2, raised, "Final Intensity changed without one config change");
                f.FinalIntensity = 0.5f;
                Assert.AreEqual(2, raised, "Final Intensity set again to the same value raised another");

                f.UseAlternateAddress = true;
                Assert.AreEqual(3, raised, "the alternate address switched on without one config change");
                f.UseAlternateAddress = true;
                Assert.AreEqual(3, raised, "the alternate address switched on again raised another");

                f.MaxIntensity = f.MaxIntensity;
                f.Range        = f.Range;
                f.MinSpotAngle = f.MinSpotAngle;
                f.MaxSpotAngle = f.MaxSpotAngle;
                Assert.AreEqual(3, raised, "a config setter given the current value raised a config change");
                f.MaxIntensity = 3f;
                f.Range        = 7f;
                f.MinSpotAngle = 9f;
                f.MaxSpotAngle = 33f;
                Assert.AreEqual(7, raised, "the four config setters didn't raise one config change each");
            }
            finally
            {
                VRSLDMXRealtimeLight.ConfigChanged -= Count;
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>RP3. The DMX realtime light's alternate address: beam and body
        /// follow it from the second frame through either addressing mode, pan
        /// inversion follows the alternate setting, and switching off brings all of
        /// it back.</summary>
        [UnityTest]
        public IEnumerator RP3_a_realtime_light_moves_to_its_alternate_address_and_back()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                var bySector = rig.Fixtures[3];
                bySector.alternateSector    = 10;
                bySector.invertPan          = true;
                bySector.alternateInvertPan = false;

                // Sector 5's channels by channel and universe, so the alternate can be
                // universe 2 channel 1, flat 521, which is sector 40.
                var byChannel = rig.Fixtures[5];
                byChannel.useLegacySectorMode  = false;
                byChannel.dmxChannel           = VRSLDMXRig.ChannelOf(5);
                byChannel.dmxUniverse          = 1;
                byChannel.alternateDmxChannel  = 1;
                byChannel.alternateDmxUniverse = 2;
                byChannel.invertPan            = false;
                byChannel.alternateInvertPan   = true;

                foreach (var f in rig.Fixtures) f.ApplyAddress();
                yield return RampRig(rig, universes: 2);

                var home = rig.ReadLights();
                Assert.IsFalse(Near(Rgb(home[3]), Rgb(home[10])),
                    "sectors 3 and 10 must decode different colours, or matching them proves nothing");
                Assert.IsFalse(Near(Rgb(home[5]), Rgb(home[40])),
                    "sectors 5 and 40 must decode different colours, or matching them proves nothing");
                Assert.AreEqual(1f, ConfigOf(rig, bySector).panSettings.z, "sector 3's pan isn't inverted at home");
                Assert.AreEqual(0f, ConfigOf(rig, byChannel).panSettings.z, "sector 5's pan is inverted at home");

                bySector.UseAlternateAddress  = true;
                byChannel.UseAlternateAddress = true;
                yield return rig.Step(2);
                var away = rig.ReadLights();
                AssertNear(Rgb(away[10]), Rgb(away[3]),
                    "the beam on its alternate sector 10 must decode sector 10's colour");
                AssertNear(Rgb(away[40]), Rgb(away[5]),
                    "the beam on its alternate universe 2 channel 1 must decode sector 40's colour");
                AssertShellsInt(bySector, "_DMXChannel", VRSLDMXRig.ChannelOf(10),
                    "the body must read the alternate sector 10");
                AssertShellsInt(byChannel, "_DMXChannel", 521,
                    "the body must read the alternate universe 2 channel 1, flat 521");
                Assert.AreEqual(0f, ConfigOf(rig, bySector).panSettings.z,
                    "sector 3 on its alternate address still pans inverted");
                Assert.AreEqual(1f, ConfigOf(rig, byChannel).panSettings.z,
                    "sector 5 on its alternate address doesn't pan inverted");
                AssertShellsInt(bySector, "_PanInvert", 0, "the body on the alternate address still pans inverted");
                AssertShellsInt(byChannel, "_PanInvert", 1, "the body on the alternate address doesn't pan inverted");

                bySector.UseAlternateAddress  = false;
                byChannel.UseAlternateAddress = false;
                yield return rig.Step(2);
                var back = rig.ReadLights();
                AssertNear(Rgb(home[3]), Rgb(back[3]), "sector 3's beam didn't come back to its own colour");
                AssertNear(Rgb(home[5]), Rgb(back[5]), "sector 5's beam didn't come back to its own colour");
                AssertShellsInt(bySector, "_DMXChannel", VRSLDMXRig.ChannelOf(3), "sector 3's body didn't come back");
                AssertShellsInt(byChannel, "_DMXChannel", VRSLDMXRig.ChannelOf(5), "sector 5's body didn't come back");
                Assert.AreEqual(1f, ConfigOf(rig, bySector).panSettings.z, "sector 3's pan inversion didn't come back");
                Assert.AreEqual(0f, ConfigOf(rig, byChannel).panSettings.z, "sector 5's pan inversion didn't come back");
                AssertShellsInt(bySector, "_PanInvert", 1, "sector 3's body pan inversion didn't come back");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>RP4. DMX Static's alternate address, in both addressing modes, with
        /// pan inversion following the alternate setting.</summary>
        [UnityTest]
        public IEnumerator RP4_a_shader_driven_fixture_moves_to_its_alternate_address_and_back()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(FlasherPrefab);
            Assert.IsNotNull(prefab, $"Asset not found: {FlasherPrefab}");
            var go = Object.Instantiate(prefab);
            try
            {
                yield return null;                              // Start sets the first block
                var f = go.GetComponentInChildren<VRSLDMXStatic>();
                Assert.IsNotNull(f, "the flasher prefab has no VRSLDMXStatic");
                Assert.Greater(f.objRenderers.Length, 1, "the row wants a fixture with more than one renderer");

                void AssertAll(string name, int expected, string because)
                {
                    foreach (var r in f.objRenderers) Assert.AreEqual(expected, BlockInt(r, name), because);
                }

                AssertAll("_DMXChannel", 1, "the flasher ships on channel 1");
                f.alternateDmxChannel  = 27;
                f.alternateDmxUniverse = 2;
                f.UseAlternateAddress  = true;
                AssertAll("_DMXChannel", 27 + 520, "the alternate universe 2 channel 27 is flat 547");
                f.UseAlternateAddress = false;
                AssertAll("_DMXChannel", 1, "the flasher didn't come back to channel 1");

                f.useLegacySectorMode = true;
                f.singleChannelMode   = true;
                f.sector              = 0;
                f.Channel             = 4;
                f.alternateSector     = 3;
                f.invertPan           = true;
                f.alternateInvertPan  = false;
                f.ApplyAddress();
                AssertAll("_DMXChannel", 1 + 4, "sector 0, channel 4 of 13 is flat 5");
                AssertAll("_PanInvert", 1, "the flasher doesn't pan inverted at home");
                f.UseAlternateAddress = true;
                AssertAll("_DMXChannel", 3 * 13 + 1 + 4, "the alternate sector 3, channel 4 of 13 is flat 44");
                AssertAll("_PanInvert", 0, "the flasher on its alternate address still pans inverted");
                f.UseAlternateAddress = false;
                AssertAll("_DMXChannel", 1 + 4, "the flasher didn't come back to flat 5");
                AssertAll("_PanInvert", 1, "the flasher's pan inversion didn't come back");
            }
            finally { Object.DestroyImmediate(go); }
        }

        static TDelegate Setter<TDelegate>(Object target, string property) where TDelegate : System.Delegate
            => (TDelegate)System.Delegate.CreateDelegate(
                typeof(TDelegate), target, target.GetType().GetProperty(property).SetMethod);

        /// <summary>RP5. The shape a Basis world script uses: a uGUI Slider and Toggle
        /// whose persistent listeners call the light's setters, driven by setting the
        /// control's value.</summary>
        [UnityTest]
        public IEnumerator RP5_uGUI_persistent_listeners_drive_the_light()
        {
            var rig = VRSLDMXRig.Build();
            GameObject ui = null;
            try
            {
                var f = rig.Fixtures[3];
                f.alternateSector = 10;
                yield return RampRig(rig);
                var before = rig.ReadLights();
                Assert.IsFalse(Near(Rgb(before[3]), Rgb(before[10])),
                    "sectors 3 and 10 must decode different colours, or matching them proves nothing");

                ui = new GameObject("VRSL RP5 relays");
                var slider = new GameObject("Strength", typeof(RectTransform)).AddComponent<Slider>();
                slider.transform.SetParent(ui.transform, false);
                var toggle = new GameObject("Second patch", typeof(RectTransform)).AddComponent<Toggle>();
                toggle.transform.SetParent(ui.transform, false);
                toggle.isOn = false;

                UnityEditor.Events.UnityEventTools.AddPersistentListener(slider.onValueChanged,
                    Setter<UnityAction<float>>(f, nameof(VRSLDMXRealtimeLight.GlobalIntensity)));
                UnityEditor.Events.UnityEventTools.AddPersistentListener(toggle.onValueChanged,
                    Setter<UnityAction<bool>>(f, nameof(VRSLDMXRealtimeLight.UseAlternateAddress)));
                Assert.AreEqual(1, slider.onValueChanged.GetPersistentEventCount(), "the slider holds no persistent call");
                Assert.AreEqual("set_GlobalIntensity", slider.onValueChanged.GetPersistentMethodName(0));
                Assert.AreEqual("set_UseAlternateAddress", toggle.onValueChanged.GetPersistentMethodName(0));

                slider.value = 0.3f;
                Assert.AreEqual(0.3f, f.globalIntensity, 1e-6f, "the slider's call didn't set Global Intensity");
                yield return rig.Step(2);
                var after = rig.ReadLights();
                AssertRatio(0.3f, before[3].colorAndIntensity.w, after[3].colorAndIntensity.w,
                    "the slider's Global Intensity didn't reach the light");

                toggle.isOn = true;
                Assert.IsTrue(f.UseAlternateAddress, "the toggle's call didn't switch the alternate address on");
                yield return rig.Step(2);
                after = rig.ReadLights();
                AssertNear(Rgb(after[10]), Rgb(after[3]),
                    "the toggle's alternate address didn't reach the beam");
            }
            finally
            {
                if (ui != null) Object.DestroyImmediate(ui);
                rig.Dispose();
            }
        }

        /// <summary>RP6. A fixture saved before the alternate address existed loads on
        /// its usual address, with everything else as it was saved.</summary>
        [Test]
        public void RP6_a_fixture_saved_before_the_alternate_address_loads_on_its_usual_address()
        {
            const string NewFields = @"^\s+(alternate\w+|useAlternateAddress):.*\r?\n";
            var spot = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(SpotPrefab)
                                  .GetComponentInChildren<VRSLDMXRealtimeLight>(true);
            Assert.IsNotNull(spot, "the spot prefab carries no DMX realtime light");
            VRSLBeamSwitchTests.WithPrefabSavedWithout(SpotPrefab, NewFields, prefab =>
            {
                var f = prefab.GetComponentInChildren<VRSLDMXRealtimeLight>(true);
                Assert.IsNotNull(f, "the copied spot carries no DMX realtime light");
                Assert.IsFalse(f.UseAlternateAddress, "a spot saved before the switch loads on its alternate address");
                Assert.AreEqual(spot.ComputeAbsoluteChannel(), f.ComputeAbsoluteChannel(), "the spot's address moved");
                Assert.AreEqual(spot.invertPan, f.invertPan, "the spot's pan inversion moved");
                Assert.AreEqual(spot.globalIntensity, f.GlobalIntensity, "the spot's Global Intensity moved");
                Assert.AreEqual(spot.finalIntensity, f.FinalIntensity, "the spot's Final Intensity moved");
            });

            var flasher = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(FlasherPrefab)
                                     .GetComponentInChildren<VRSLDMXStatic>(true);
            Assert.IsNotNull(flasher, "the flasher prefab carries no DMX Static");
            VRSLBeamSwitchTests.WithPrefabSavedWithout(FlasherPrefab, NewFields, prefab =>
            {
                var f = prefab.GetComponentInChildren<VRSLDMXStatic>(true);
                Assert.IsNotNull(f, "the copied flasher carries no DMX Static");
                Assert.IsFalse(f.UseAlternateAddress, "a flasher saved before the switch loads on its alternate address");
                Assert.AreEqual(flasher.dmxChannel, f.dmxChannel, "the flasher's channel moved");
                Assert.AreEqual(flasher.dmxUniverse, f.dmxUniverse, "the flasher's universe moved");
                Assert.AreEqual(flasher.sector, f.sector, "the flasher's sector moved");
            });
        }
    }
}
#endif
