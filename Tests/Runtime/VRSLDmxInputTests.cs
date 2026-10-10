#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The DMX manager's DMX Input: three grids and Stream data, chosen while the world
    /// runs, a channel source on standby under a grid, and Stream data dark when nothing
    /// is sending it. Rows E1 to E5 of TESTING.md (E6 is in VRSLGridLayoutTests).
    /// </summary>
    class VRSLDmxInputTests : VRSLDMXTest
    {
        static readonly int s_GridLayout = Shader.PropertyToID("_VRSLU_DMXGridLayout");

        const string QuietMessage = "Stream data is chosen but nothing in this scene is sending it";

        /// <summary>What the source last published over the channels the rig patches,
        /// as a pattern <see cref="VRSLGridSwitchTests.Mismatches"/> can judge.</summary>
        static float[] Published(VRSLDMXRig rig)
        {
            var flat   = rig.Manager.PublishedChannels.ToArray();
            var values = new float[VRSLGridSwitchTests.PatchedChannels];
            for (int i = 0; i < values.Length && i < flat.Length; i++) values[i] = flat[i] / 255f;
            return values;
        }

        /// <summary>Counts the manager's message about Stream data with nothing to read.
        /// Warnings don't fail a row on their own, so "none before the grace period" has
        /// to be counted.</summary>
        sealed class QuietCounter : System.IDisposable
        {
            public int Count;
            public QuietCounter() => Application.logMessageReceived += OnLog;
            void OnLog(string message, string stack, LogType type)
            {
                if (type == LogType.Warning && message.Contains(QuietMessage)) Count++;
            }
            public void Dispose() => Application.logMessageReceived -= OnLog;
        }

        static IEnumerator Frames(VRSLDMXRig rig, int count)
        {
            for (int i = 0; i < count; i++)
            {
                yield return null;
                rig.RenderFrame();
            }
        }

        static void AllDark(VRSLDMXRig rig, string when)
        {
            var lights = rig.ReadLights();
            for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                Assert.AreEqual(Vector4.zero, lights[i].colorAndIntensity,
                    $"fixture {i} still lights {when}");
        }

        /// <summary>
        /// E1. The rig with the synthetic source attached and each layout's video
        /// grid painted with its own pattern through the shipped decode chain, cycled
        /// Stream data, Horizontal, Vertical, Legacy, Stream data, Horizontal. Every
        /// fixture lights the chosen input's values: Stream data from the next frame, a
        /// grid within its chain's settle time. Under a grid the source stays bound and
        /// publishes nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator E1_every_input_lights_its_own_values_through_a_cycle()
        {
            var asset = VRSLGridSwitchTests.Shipped();
            var modes = VRSLGridSwitchTests.Snapshot(asset);
            var rig   = VRSLDMXRig.Build();
            try
            {
                Assert.AreSame(asset, rig.Manager.gridTextures, "the rig's manager is not on the shipped Grid Textures");
                Assert.AreEqual(VRSLDmxInput.StreamData, rig.Manager.dmxInput, "the rig did not choose Stream data");
                yield return rig.Step(4);
                rig.Calibrate();

                foreach (var layout in VRSLGridSwitchTests.Layouts) VRSLDMXRig.ClearGridTextures(asset.For(layout));
                yield return VRSLGridSwitchTests.PastTheChainsStartUp(rig);
                var patterns = VRSLGridSwitchTests.Layouts.ToDictionary(l => l, VRSLGridSwitchTests.Pattern);
                foreach (var layout in VRSLGridSwitchTests.Layouts)
                    VRSLDMXRig.PaintVideoGrid(asset.For(layout), layout, patterns[layout]);
                rig.Manager.disableStrobe = true;
                rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
                rig.Source.speed   = 0f;
                yield return Frames(rig, 4);

                var stream = Published(rig);
                Assert.IsEmpty(VRSLGridSwitchTests.Mismatches(rig, stream), "the rig does not light the source's values");
                foreach (var layout in VRSLGridSwitchTests.Layouts)
                    Assert.IsNotEmpty(VRSLGridSwitchTests.Mismatches(rig, patterns[layout]),
                        $"the source's values match the {layout} pattern, so the row could not tell them apart");

                var wrong  = new List<string>();
                var frames = new int[1];
                foreach (var input in new[] { VRSLDmxInput.StreamData, VRSLDmxInput.HorizontalGrid,
                                              VRSLDmxInput.VerticalGrid, VRSLDmxInput.LegacyGrid,
                                              VRSLDmxInput.StreamData, VRSLDmxInput.HorizontalGrid })
                {
                    rig.Manager.SetDmxInput((int)input);
                    Assert.AreEqual(input, rig.Manager.dmxInput);
                    if (input == VRSLDmxInput.StreamData)
                    {
                        yield return Frames(rig, 1);
                        wrong.Clear();
                        wrong.AddRange(VRSLGridSwitchTests.Mismatches(rig, stream));
                        Assert.IsEmpty(wrong, "the frame after choosing Stream data, fixtures don't light the "
                                            + "source's values: " + string.Join("; ", wrong.Take(8)));
                        Assert.Greater(rig.Manager.ChannelCount, 0);
                        Debug.Log("[E1] Stream data read the source the next frame");
                    }
                    else
                    {
                        var layout = (VRSLGridLayout)input;
                        yield return VRSLGridSwitchTests.Settle(rig, patterns[layout], wrong, frames);
                        Assert.IsEmpty(wrong, $"after choosing {input}, {wrong.Count} fixtures don't light that "
                                            + "layout's pattern: " + string.Join("; ", wrong.Take(8)));
                        Debug.Log($"[E1] {input} decoded its own grid {frames[0]} frames after the switch");
                        Assert.LessOrEqual(frames[0], layout == VRSLGridLayout.Legacy ? 10 : 5,
                            $"{input} took longer than its chain's settle time");
                        Assert.AreEqual(0, rig.Manager.ChannelCount, $"the source is published under {input}");
                        Assert.AreEqual((int)layout, Shader.GetGlobalInteger(s_GridLayout));
                    }
                    Assert.IsTrue(ReferenceEquals(rig.Source, rig.Manager.ChannelSource),
                        $"choosing {input} detached the source");
                }
            }
            finally
            {
                rig.Dispose();
                foreach (var layout in VRSLGridSwitchTests.Layouts) VRSLDMXRig.ClearGridTextures(asset.For(layout));
                VRSLGridSwitchTests.Restore(modes);
            }
        }

        /// <summary>
        /// E2, E1's image half. N29's and N30's scenes, with the synthetic source and a
        /// grid of other values both there, switched at runtime from Stream data to the
        /// Horizontal grid and from the grid to Stream data: each frame matches the same
        /// scene brought up on that input, shader-driven bodies and realtime lights alike.
        /// </summary>
        [UnityTest]
        public IEnumerator E2_the_fixtures_render_an_input_switched_to_as_one_brought_up_on_it()
        {
            int count = VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 25;
            var grid  = VRSLGridDecodeTests.ImageValues(count);
            var sets = new[]
            {
                (VRSLGridDecodeTests.UrpIncludePrefabs, VRSLGridDecodeTests.UrpIncludeFacing, new Vector3(6f, 2f, -15f)),
                (VRSLGridDecodeTests.BuiltInIncludePrefabs,
                 new Vector3[VRSLGridDecodeTests.BuiltInIncludePrefabs.Length], new Vector3(4.4f, 2f, -12f)),
            };
            var fixtures = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
            foreach (var (prefabs, facing, eye) in sets)
            foreach (var (from, to) in new[] { (VRSLDmxInput.StreamData, VRSLDmxInput.HorizontalGrid),
                                               (VRSLDmxInput.HorizontalGrid, VRSLDmxInput.StreamData) })
            {
                Texture2D switched = null, alone = null, other = null;
                try
                {
                    yield return VRSLGridDecodeTests.CaptureLayout(prefabs, VRSLGridLayout.Horizontal, grid,
                        t => switched = t, switchAtRuntime: true, source: fixtures, facing: facing, eye: eye,
                        input: to, startOn: from);
                    yield return VRSLGridDecodeTests.CaptureLayout(prefabs, VRSLGridLayout.Horizontal, grid,
                        t => alone = t, source: fixtures, facing: facing, eye: eye, input: to);
                    yield return VRSLGridDecodeTests.CaptureLayout(prefabs, VRSLGridLayout.Horizontal, grid,
                        t => other = t, source: fixtures, facing: facing, eye: eye, input: from);

                    var sensitivity = VRSLImageCompare.Compare(alone, other);
                    Assert.Greater(sensitivity.DifferingPercent, 1f,
                        $"{from} and {to} render alike ({sensitivity}), so the row could not see a switch fail");
                    var result = VRSLImageCompare.Compare(alone, switched);
                    Debug.Log($"[E2] {prefabs[0]} and the rest, {from} switched to {to} against {to} alone: "
                            + $"{result}; {from} against {to}: {sensitivity}");
                    if (result.DifferingPercent > VRSLGridDecodeTests.SameValuesBudget)
                        VRSLImageCompare.WriteImages(VRSLGridDecodeTests.EvidenceFolder, $"E2-{from}-{to}", alone, switched);
                    Assert.LessOrEqual(result.DifferingPercent, VRSLGridDecodeTests.SameValuesBudget,
                        $"switched from {from} to {to}, the frame differs from one brought up on {to} ({result}). "
                      + $"Images in {VRSLGridDecodeTests.EvidenceFolder}");
                }
                finally
                {
                    if (switched != null) Object.DestroyImmediate(switched);
                    if (alone    != null) Object.DestroyImmediate(alone);
                    if (other    != null) Object.DestroyImmediate(other);
                }
            }
        }

        /// <summary>
        /// E3. A number outside 0 to 3 is refused with one warning and changes
        /// nothing, on Stream data as on a grid. Stream data is never refused, with or
        /// without a source. (G12 covers a grid the Grid Textures can't decode.)
        /// </summary>
        [UnityTest]
        public IEnumerator E3_a_number_that_is_no_input_changes_nothing()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                foreach (int bad in new[] { 7, -1, 4 })
                {
                    LogAssert.Expect(LogType.Warning, new Regex($"There's no DMX input {bad}\\. .*The lights stay on Stream data"));
                    rig.Manager.SetDmxInput(bad);
                    Assert.AreEqual(VRSLDmxInput.StreamData, rig.Manager.dmxInput);
                    yield return rig.Step(1);
                    Assert.Greater(rig.Manager.ChannelCount, 0, $"input {bad} stopped the source being read");
                }

                rig.Manager.SetDmxInput((int)VRSLDmxInput.VerticalGrid);
                LogAssert.Expect(LogType.Warning, new Regex("There's no DMX input 9\\. .*The lights stay on the Vertical grid"));
                rig.Manager.SetDmxInput(9);
                Assert.AreEqual(VRSLDmxInput.VerticalGrid, rig.Manager.dmxInput);

                rig.Manager.ChannelSource = null;
                rig.Manager.SetDmxInput((int)VRSLDmxInput.StreamData);
                Assert.AreEqual(VRSLDmxInput.StreamData, rig.Manager.dmxInput, "Stream data with no source was refused");
                yield return rig.Step(1);
            }
            finally { rig.Dispose(); }
        }

        /// <summary>
        /// E4. Stream data with nothing sending it: the source switched off, then
        /// the lights are dark from the next frames on, and the manager says why once,
        /// after the grace period and not before.
        /// </summary>
        [UnityTest]
        public IEnumerator E4_stream_data_with_nothing_sending_it_is_dark_and_says_so_once()
        {
            var rig = VRSLDMXRig.Build();
            using var quiet = new QuietCounter();
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
                yield return rig.Step(2);
                Assert.IsTrue(rig.ReadLights().Any(l => l.colorAndIntensity.w > 0f), "nothing lit from the source");

                rig.Source.enabled = false;
                yield return rig.Step(2);
                AllDark(rig, "with Stream data chosen and its source switched off");

                yield return rig.Step(VRSLDMXRig.Frames(VRSL_URPLightManager.StreamQuietGrace - 1f));
                Assert.AreEqual(0, quiet.Count, "the manager spoke before the grace period was up");
                AllDark(rig, "a few seconds later");

                yield return rig.Step(VRSLDMXRig.Frames(2f));
                Assert.AreEqual(1, quiet.Count, "the manager did not say once why the lights are dark");
                yield return rig.Step(VRSLDMXRig.Frames(VRSL_URPLightManager.StreamQuietGrace + 1f));
                Assert.AreEqual(1, quiet.Count, "the manager said it more than once");

                rig.Source.enabled = true;
                rig.Manager.ChannelSource = rig.Source;
                yield return rig.Step(2);
                Assert.IsTrue(rig.ReadLights().Any(l => l.colorAndIntensity.w > 0f),
                    "the source came back and nothing lit from it");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>
        /// E5. Applying an input the way a networked script does ends
        /// in the state of applying it once at the right moment: twice in a row; Stream
        /// data before the source enables; on a manager that doesn't own the singleton,
        /// which reads it once it takes over. Nothing logs. On a hand-over the successor
        /// keeps its own input and the source moves with the singleton.
        /// </summary>
        [UnityTest]
        public IEnumerator E5_an_input_applied_twice_early_or_to_a_spare_manager_lands_once()
        {
            var shipped  = VRSLGridSwitchTests.Shipped();
            var modes    = VRSLGridSwitchTests.Snapshot(shipped);
            var rig      = VRSLDMXRig.Build(withSource: false);
            var spareGo  = new GameObject("E5 spare manager");
            spareGo.SetActive(false);
            GameObject lateGo = null;
            using var quiet = new QuietCounter();
            try
            {
                yield return rig.Step(2);

                // Twice.
                rig.Manager.SetDmxInput((int)VRSLDmxInput.VerticalGrid);
                var live  = rig.Manager.LiveTextures.main;
                var mode  = ((CustomRenderTexture)live).updateMode;
                rig.Manager.SetDmxInput((int)VRSLDmxInput.VerticalGrid);
                yield return rig.Step(1);
                Assert.AreEqual(VRSLDmxInput.VerticalGrid, rig.Manager.dmxInput);
                Assert.AreSame(live, rig.Manager.LiveTextures.main);
                Assert.AreEqual(mode, ((CustomRenderTexture)live).updateMode);
                Assert.AreEqual((int)VRSLGridLayout.Vertical, Shader.GetGlobalInteger(s_GridLayout));

                // Stream data first, then the source a moment later, as a world applying the
                // synced value on join might.
                rig.Manager.SetDmxInput((int)VRSLDmxInput.StreamData);
                yield return rig.Step(VRSLDMXRig.Frames(1f));
                lateGo = new GameObject("E5 late source");
                lateGo.transform.SetParent(rig.Manager.transform.parent, false);
                var late = lateGo.AddComponent<VRSL_SyntheticDMXChannelSource>();
                late.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
                yield return rig.Step(2);
                Assert.IsTrue(ReferenceEquals(late, rig.Manager.ChannelSource), "the late source did not bind");
                Assert.Greater(rig.Manager.ChannelCount, 0, "Stream data chosen early did not read the late source");

                // A spare manager, switched on after the owner: it takes the setting only.
                var spare = spareGo.AddComponent<VRSL_URPLightManager>();
                spare.enabled = false;
                spareGo.SetActive(true);
                spare.gridTextures = shipped;
                spare.enabled = true;
                yield return null;
                Assert.AreSame(rig.Manager, VRSL_URPLightManager.Instance, "the spare took the singleton");
                spare.SetDmxInput((int)VRSLDmxInput.LegacyGrid);
                spare.SetDmxInput((int)VRSLDmxInput.LegacyGrid);
                Assert.AreEqual(VRSLDmxInput.LegacyGrid, spare.dmxInput);
                yield return rig.Step(1);
                Assert.Greater(rig.Manager.ChannelCount, 0, "the spare's setting reached the owner");

                // The hand-over: the source moves, the spare keeps its own input and puts
                // the source on standby.
                rig.Manager.enabled = false;
                yield return null;
                Assert.AreSame(spare, VRSL_URPLightManager.Instance, "the spare was not handed the singleton");
                Assert.AreEqual(VRSLDmxInput.LegacyGrid, spare.dmxInput, "the successor took the outgoing input");
                Assert.IsTrue(ReferenceEquals(late, spare.ChannelSource), "the source did not move with the singleton");
                yield return null;
                Assert.AreEqual(0, spare.ChannelCount, "the successor publishes the source on a grid input");
                Assert.AreEqual((int)VRSLGridLayout.Legacy, Shader.GetGlobalInteger(s_GridLayout));
                Assert.AreSame(shipped.legacy.main, spare.LiveTextures.main);

                Assert.AreEqual(0, quiet.Count, "the manager complained about Stream data during load order");
            }
            finally
            {
                if (lateGo != null) Object.DestroyImmediate(lateGo);
                Object.DestroyImmediate(spareGo);
                rig.Dispose();
                VRSLGridSwitchTests.Restore(modes);
            }
        }
    }
}
#endif
