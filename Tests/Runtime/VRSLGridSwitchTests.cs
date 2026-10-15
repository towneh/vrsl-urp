#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Switching the DMX manager's grid layout while the world runs. Rows G11 to G13 of
    /// TESTING.md.
    /// </summary>
    class VRSLGridSwitchTests : VRSLDMXTest
    {
        internal static readonly VRSLGridLayout[] Layouts =
            { VRSLGridLayout.Horizontal, VRSLGridLayout.Vertical, VRSLGridLayout.Legacy };

        static readonly int s_GridLayout = Shader.PropertyToID("_VRSLU_DMXGridLayout");

        /// <summary>The last channel the rig patches: its last fixture's thirteenth.</summary>
        internal static int PatchedChannels => VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 12;

        /// <summary>Video values arrive through an 8-bit grid and the decode chain's own
        /// formats, so a few byte steps rather than half of one.</summary>
        internal const float VideoTolerance = 3f / 255f;

        internal static VRSLGridTextures Shipped()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VRSLGridTextures>(
                AssetDatabase.GUIDToAssetPath(VRSLWiring.GridTexturesGuid));
            Assert.IsNotNull(asset, "the shipped Grid Textures asset is not in this project");
            return asset;
        }

        static IEnumerable<CustomRenderTexture> Crts(VRSLGridTextures asset) =>
            Layouts.Select(asset.For)
                   .SelectMany(s => new[] { s.main, s.movement, s.strobe, s.strobeTimer, s.spinTimer })
                   .OfType<CustomRenderTexture>();

        // In the editor an update mode set on an asset is written to the asset, so every
        // row here puts the originals back however it ends.
        internal static Dictionary<CustomRenderTexture, CustomRenderTextureUpdateMode> Snapshot(VRSLGridTextures asset)
            => Crts(asset).Distinct().ToDictionary(c => c, c => c.updateMode);

        internal static void Restore(Dictionary<CustomRenderTexture, CustomRenderTextureUpdateMode> modes)
        {
            foreach (var pair in modes) if (pair.Key != null) pair.Key.updateMode = pair.Value;
        }

        static bool Strobes(int fixture) => fixture % 5 == 0;

        /// <summary>
        /// A pattern of channel values different in every layout, so a fixture reading
        /// the grid of the layout it just left shows it. Every fixture's dimmer is full;
        /// every fifth fixture's strobe channel is high and the rest are at zero, which
        /// holds them on.
        /// </summary>
        internal static float[] Pattern(VRSLGridLayout layout)
        {
            var values = new float[PatchedChannels];
            int k = (int)layout;
            for (int ch = 1; ch <= values.Length; ch++)
                values[ch - 1] = ((ch * 7 + 53 * k) % 180 + 50) / 255f;
            for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
            {
                int abs = VRSLDMXRig.ChannelOf(i);
                values[VRSLDMXRig.DimmerChannel(abs) - 1] = 1f;
                values[VRSLDMXRig.StrobeChannel(abs) - 1] = Strobes(i) ? 0.8f : 0f;
            }
            return values;
        }

        static Vector3 Expected(float[] pattern, int fixture)
        {
            int red = VRSLDMXRig.RedChannel(VRSLDMXRig.ChannelOf(fixture));
            return new Vector3(pattern[red - 1], pattern[red], pattern[red + 1]);
        }

        static Vector3 Rgb(VRSL_DMXURPLightManager.VRSLLightData d)
            => new Vector3(d.colorAndIntensity.x, d.colorAndIntensity.y, d.colorAndIntensity.z);

        internal static List<string> Mismatches(VRSLDMXRig rig, float[] pattern)
        {
            var lights = rig.ReadLights();
            var wrong = new List<string>();
            for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
            {
                var want = Expected(pattern, i);
                var got  = Rgb(lights[i]);
                if (Mathf.Abs(want.x - got.x) > VideoTolerance || Mathf.Abs(want.y - got.y) > VideoTolerance
                    || Mathf.Abs(want.z - got.z) > VideoTolerance)
                    wrong.Add($"fixture {i} lit {got * 255f}, expected {want * 255f}");
            }
            return wrong;
        }

        /// <summary>Step until every fixture lights the pattern, or give up after four
        /// seconds: the incoming chain glides from what it last held to the live grid.
        /// The frames it took go in <paramref name="frames"/>[0].</summary>
        internal static IEnumerator Settle(VRSLDMXRig rig, float[] pattern, List<string> wrong, int[] frames)
        {
            wrong.Clear();
            for (int f = 1; f <= 240; f++)
            {
                yield return null;
                rig.RenderFrame();
                if (f % 5 != 0) continue;
                wrong.Clear();
                wrong.AddRange(Mismatches(rig, pattern));
                frames[0] = f;
                if (wrong.Count == 0) yield break;
            }
        }

        /// <summary>
        /// The game clock, handed to the decode chain. The chain's textures update
        /// between frames, outside any camera, and in batch mode nothing sets the shader
        /// clock there: they saw a <c>_Time.y</c> under a second however long the row ran,
        /// so the strobe timing never left its start-up branch and the strobe output held
        /// open. Set in <c>LateUpdate</c>, which runs before they update.
        /// </summary>
        class ChainClock : MonoBehaviour
        {
            static readonly int s_Time      = Shader.PropertyToID("_Time");
            static readonly int s_DeltaTime = Shader.PropertyToID("unity_DeltaTime");

            void LateUpdate()
            {
                float t = Time.timeSinceLevelLoad, dt = Time.deltaTime;
                Shader.SetGlobalVector(s_Time, new Vector4(t / 20f, t, t * 2f, t * 3f));
                Shader.SetGlobalVector(s_DeltaTime, new Vector4(dt, 1f / dt, dt, 1f / dt));
            }
        }

        /// <summary>The decode chain holds its first seconds back for data to arrive
        /// (the colour stage waits three, the strobe timer one), counted on the game
        /// clock since the scene loaded (`_Time.y`), which the runner restarts per test.</summary>
        internal static IEnumerator PastTheChainsStartUp(VRSLDMXRig rig)
        {
            if (rig.Manager.GetComponent<ChainClock>() == null) rig.Manager.gameObject.AddComponent<ChainClock>();
            while (Time.timeSinceLevelLoad < 3.5f)
            {
                yield return null;
                rig.RenderFrame();
            }
        }

        static IEnumerator Sample(VRSLDMXRig rig, int count, List<VRSL_DMXURPLightManager.VRSLLightData[]> into)
        {
            for (int i = 0; i < count; i++)
            {
                yield return null;
                rig.RenderFrame();
                into.Add(rig.ReadLights());
            }
        }

        /// <summary>
        /// G11. A rig switched Horizontal, Vertical, Legacy and back to
        /// Horizontal while it runs, each layout's video grid carrying its own pattern
        /// through that layout's real decode chain, lights each layout's pattern on the
        /// right fixtures, and strobes from the live layout's strobe timing.
        ///
        /// The strobe half is what catches a manager publishing the wrong layout's
        /// timing: every layout but the live one is stopped, so a timing texture left
        /// over from another layout holds still and the strobing fixtures never change.
        /// </summary>
        [UnityTest]
        public IEnumerator G11_a_runtime_switch_lights_each_layouts_own_grid()
        {
            var asset = Shipped();
            var modes = Snapshot(asset);
            var rig = VRSLDMXRig.Build(withSource: false);
            try
            {
                Assert.AreSame(asset, rig.Manager.gridTextures, "the rig's manager is not on the shipped Grid Textures");
                Assert.AreEqual(VRSLDmxInput.HorizontalGrid, rig.Manager.dmxInput);
                foreach (var layout in Layouts) VRSLDMXRig.ClearGridTextures(asset.For(layout));
                yield return PastTheChainsStartUp(rig);

                var patterns = Layouts.ToDictionary(l => l, Pattern);
                foreach (var layout in Layouts)
                    VRSLDMXRig.PaintVideoGrid(asset.For(layout), layout, patterns[layout]);
                rig.Manager.disableStrobe = true;
                yield return rig.Step(4);
                rig.Calibrate();

                var wrong  = new List<string>();
                var frames = new int[1];
                foreach (var layout in new[] { VRSLGridLayout.Horizontal, VRSLGridLayout.Vertical,
                                               VRSLGridLayout.Legacy, VRSLGridLayout.Horizontal })
                {
                    rig.Manager.disableStrobe = true;
                    // What a switch costs: the bind runs inside the call, and the refresh
                    // that follows is a fixture search when the fixtures are unchanged, as here.
                    bool switching = layout != rig.Manager.GridLayout;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    rig.Manager.SetDmxInput((int)layout);
                    double bindMs = watch.Elapsed.TotalMilliseconds;
                    watch.Restart();
                    Object.FindObjectsByType<VRSLDMXRealtimeLight>(FindObjectsInactive.Exclude);
                    double searchMs = watch.Elapsed.TotalMilliseconds;
                    if (switching)
                        Debug.Log($"[G11] switch to {layout}: bound in {bindMs:F3} ms, fixture search {searchMs:F3} ms "
                                + $"({VRSLDMXRig.FixtureCount} fixtures)");
                    Assert.AreEqual(layout, rig.Manager.GridLayout);
                    Assert.AreEqual((int)layout, Shader.GetGlobalInteger(s_GridLayout),
                        $"the switch to {layout} did not publish the layout");
                    Assert.AreSame(asset.For(layout).main, rig.Manager.LiveTextures.main);

                    yield return Settle(rig, patterns[layout], wrong, frames);
                    Assert.IsEmpty(wrong, $"after the switch to {layout}, {wrong.Count} fixtures do not light "
                                        + "that layout's pattern: " + string.Join("; ", wrong.Take(8)));
                    Debug.Log($"[G11] {layout} decoded its own grid {frames[0]} frames after the switch");

                    rig.Manager.disableStrobe = false;
                    var samples = new List<VRSL_DMXURPLightManager.VRSLLightData[]>();
                    yield return Sample(rig, 4, samples);
                    samples.Clear();
                    yield return Sample(rig, 60, samples);
                    var still = new List<string>();
                    var dark  = new List<string>();
                    for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                    {
                        var on = samples.Select(s => s[i].colorAndIntensity.w > 0f).ToArray();
                        if (Strobes(i) && on.Distinct().Count() < 2)
                            still.Add($"{i} (always {(on[0] ? "on" : "off")})");
                        if (!Strobes(i) && !on.All(x => x))
                            dark.Add(i.ToString());
                    }
                    int strobing = Enumerable.Range(0, VRSLDMXRig.FixtureCount).Count(Strobes);
                    if (layout == VRSLGridLayout.Legacy)
                    {
                        // The Legacy strobe textures are 25 by 25 against a 50 by 50 grid, as
                        // upstream ships them, so one strobe texel serves four cells and which
                        // fixtures strobe depends on the neighbour it samples. What the row can
                        // judge is that the strobe runs from Legacy's own timing: a timing
                        // texture left over from another layout holds every fixture still.
                        Debug.Log($"[G11] Legacy: {strobing - still.Count} of {strobing} strobing fixtures "
                                + $"changed state; {dark.Count} held fixtures strobed from a neighbouring cell");
                        Assert.Less(still.Count, strobing,
                            $"on Legacy no fixture changed state across {samples.Count} frames, so nothing "
                          + "strobes from Legacy's timing");
                        continue;
                    }
                    Assert.IsEmpty(still, $"on {layout}, {still.Count} fixtures never changed state across "
                        + $"{samples.Count} frames, so they are not strobing from {layout}'s timing: "
                        + string.Join(", ", still));
                    Assert.IsEmpty(dark, $"on {layout}, fixtures with their strobe channel at zero went off: "
                        + string.Join(", ", dark));
                }
            }
            finally
            {
                rig.Dispose();
                foreach (var layout in Layouts) VRSLDMXRig.ClearGridTextures(asset.For(layout));
                Restore(modes);
            }
        }

        /// <summary>
        /// G12. A switch the manager cannot carry out changes
        /// nothing and says why: a layout its Grid Textures have no textures for, by the
        /// call and by the field (the inspector, an Undo), and a number that is no
        /// layout. On a manager that does not own the singleton the call changes the
        /// setting only, and the manager reads it once it takes over.
        /// </summary>
        [UnityTest]
        public IEnumerator G12_a_switch_the_manager_cannot_make_changes_nothing()
        {
            var shipped = Shipped();
            var modes = Snapshot(shipped);
            var rig = VRSLDMXRig.Build(withSource: false);
            var partial = ScriptableObject.CreateInstance<VRSLGridTextures>();
            partial.name = "Horizontal only";
            partial.horizontal = shipped.horizontal;
            var standbyGo = new GameObject("G12 standby manager");
            standbyGo.SetActive(false);
            try
            {
                rig.Manager.enabled      = false;
                rig.Manager.gridTextures = partial;
                rig.Manager.enabled      = true;
                yield return rig.Step(2);

                LogAssert.Expect(LogType.Warning, new Regex("Can't switch to the Vertical grid: Horizontal only has no textures"));
                rig.Manager.SetDmxInput((int)VRSLDmxInput.VerticalGrid);
                Assert.AreEqual(VRSLDmxInput.HorizontalGrid, rig.Manager.dmxInput);
                Assert.AreEqual(0, Shader.GetGlobalInteger(s_GridLayout));

                LogAssert.Expect(LogType.Warning, new Regex("There's no DMX input 7"));
                rig.Manager.SetDmxInput(7);
                Assert.AreEqual(VRSLDmxInput.HorizontalGrid, rig.Manager.dmxInput);

                // The field set directly, as the inspector or an Undo does in Play mode.
                LogAssert.Expect(LogType.Warning, new Regex("Can't switch to the Legacy grid"));
                rig.Manager.dmxInput   = VRSLDmxInput.LegacyGrid;
                yield return rig.Step(1);
                Assert.AreEqual(VRSLDmxInput.HorizontalGrid, rig.Manager.dmxInput,
                    "the field was left naming a layout the manager cannot read");
                Assert.AreEqual(0, Shader.GetGlobalInteger(s_GridLayout));

                // Added switched off and then switched on: Awake destroys an enabled duplicate.
                var standby = standbyGo.AddComponent<VRSL_DMXURPLightManager>();
                standby.enabled = false;
                standbyGo.SetActive(true);
                standby.gridTextures = shipped;
                standby.enabled = true;
                yield return null;
                Assert.AreSame(rig.Manager, VRSL_DMXURPLightManager.Instance, "the standby took the singleton");

                var legacyMode = shipped.legacy.main is CustomRenderTexture legacyMain ? legacyMain.updateMode
                                                                                        : CustomRenderTextureUpdateMode.OnDemand;
                standby.SetDmxInput(2);
                Assert.AreEqual(VRSLDmxInput.LegacyGrid, standby.dmxInput, "the standby did not take the setting");
                Assert.AreEqual(0, Shader.GetGlobalInteger(s_GridLayout),
                    "a manager that does not own the singleton changed the layout global");
                Assert.AreEqual(legacyMode, ((CustomRenderTexture)shipped.legacy.main).updateMode,
                    "a manager that does not own the singleton started a layout's textures");

                rig.Manager.enabled = false;
                yield return null;
                Assert.AreSame(standby, VRSL_DMXURPLightManager.Instance, "the standby was not handed the singleton");
                Assert.AreEqual((int)VRSLGridLayout.Legacy, Shader.GetGlobalInteger(s_GridLayout),
                    "the standby took over without reading the layout it was set to");
                Assert.AreSame(shipped.legacy.main, standby.LiveTextures.main);
            }
            finally
            {
                Object.DestroyImmediate(standbyGo);
                rig.Dispose();
                Object.DestroyImmediate(partial);
                Restore(modes);
            }
        }

        /// <summary>The smallest value anywhere in a decode texture's red channel.</summary>
        static float MinOf(RenderTexture rt)
        {
            var shot = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            var was = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                shot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                shot.Apply(false);
                return shot.GetPixels().Min(c => c.r);
            }
            finally
            {
                RenderTexture.active = was;
                Object.DestroyImmediate(shot);
            }
        }

        static IEnumerator SampleMin(VRSLDMXRig rig, RenderTexture rt, int count, List<float> into)
        {
            for (int i = 0; i < count; i++)
            {
                yield return null;
                rig.RenderFrame();
                into.Add(MinOf(rt));
            }
        }

        /// <summary>Every number a material holds, as one string. Its dirty count is no
        /// use here: the decode chain's own updates raise it on every material.</summary>
        static string SavedValues(Material m) => string.Join(";",
            m.GetPropertyNames(MaterialPropertyType.Float).Select(n => $"{n}={m.GetFloat(n)}")
             .Concat(m.GetPropertyNames(MaterialPropertyType.Int).Select(n => $"{n}={m.GetInteger(n)}"))
             .Concat(m.shaderKeywords));

        /// <summary>
        /// G13. The control panel still drives the manager after a switch: its
        /// light slider scales the light the fixtures cast, and its strobe toggle holds
        /// the live layout's strobe output open, without writing to any of the decode
        /// chain's materials.
        /// </summary>
        [UnityTest]
        public IEnumerator G13_the_control_panel_drives_the_manager_after_a_switch()
        {
            var asset = Shipped();
            var modes = Snapshot(asset);
            var materials = Crts(asset).Select(c => c.material).Where(m => m != null).Distinct().ToList();
            var saved = materials.ToDictionary(m => m, SavedValues);
            var rig = VRSLDMXRig.Build(withSource: false);
            var panelGo  = new GameObject("G13 control panel");
            var sliderGo = new GameObject("G13 light slider");
            panelGo.SetActive(false);
            try
            {
                foreach (var layout in Layouts) VRSLDMXRig.ClearGridTextures(asset.For(layout));
                yield return PastTheChainsStartUp(rig);
                var pattern = Pattern(VRSLGridLayout.Vertical);
                VRSLDMXRig.PaintVideoGrid(asset.vertical, VRSLGridLayout.Vertical, pattern);
                yield return rig.Step(4);
                rig.Calibrate();

                var slider = sliderGo.AddComponent<Slider>();
                slider.value = 1f;
                var panel = panelGo.AddComponent<VRSL_LocalUIControlPanel>();
                panel.dmxManager  = rig.Manager;
                panel.lightSlider = slider;
                panelGo.SetActive(true);
                yield return null;   // the panel's Start
                Assert.IsFalse(rig.Manager.disableStrobe, "the panel started with strobe held");

                rig.Manager.SetDmxInput((int)VRSLDmxInput.VerticalGrid);
                var wrong  = new List<string>();
                var frames = new int[1];
                yield return Settle(rig, pattern, wrong, frames);
                Assert.IsEmpty(wrong, "the rig never lit the Vertical pattern: " + string.Join("; ", wrong.Take(8)));

                // Light: held-on fixtures only, so strobe does not move the reading.
                var held = Enumerable.Range(0, VRSLDMXRig.FixtureCount).Where(i => !Strobes(i)).ToArray();
                var before = rig.ReadLights();
                slider.value = 0.5f;
                panel._SetLightIntensity();
                yield return rig.Step(3);
                var after = rig.ReadLights();
                foreach (int i in held)
                    Assert.AreEqual(before[i].colorAndIntensity.w * 0.5f, after[i].colorAndIntensity.w,
                        before[i].colorAndIntensity.w * 0.01f,
                        $"fixture {i} did not halve with the panel's light slider after the switch");

                // Strobe: the live strobe output closes somewhere while strobe runs, so
                // holding it open is something the toggle has to do.
                var strobe = rig.Manager.LiveTextures.strobe;
                Assert.AreSame(asset.vertical.strobe, strobe);
                var mins = new List<float>();
                yield return SampleMin(rig, strobe, 30, mins);
                Assert.Less(mins.Min(), 0.5f,
                    "the Vertical strobe output never closed while strobing, so the row cannot judge the toggle");

                panel._ToggleGlobalStrobe();
                Assert.IsTrue(rig.Manager.disableStrobe, "the panel's toggle did not reach the manager");
                yield return rig.Step(3);
                mins.Clear();
                yield return SampleMin(rig, strobe, 30, mins);
                Assert.Greater(mins.Min(), 1f - VideoTolerance,
                    "with the panel's strobe toggle on, the live strobe output still closes");

                var written = materials.Where(m => SavedValues(m) != saved[m]).Select(m => m.name).ToList();
                Assert.IsEmpty(written, "the panel wrote to decode materials: " + string.Join(", ", written));
            }
            finally
            {
                Object.DestroyImmediate(panelGo);
                Object.DestroyImmediate(sliderGo);
                rig.Dispose();
                foreach (var layout in Layouts) VRSLDMXRig.ClearGridTextures(asset.For(layout));
                Restore(modes);
            }
        }
    }
}
#endif
