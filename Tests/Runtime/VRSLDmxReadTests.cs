#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// A shader of a world's own reading DMX through <c>Runtime/Shaders/VRSLDMX.hlsl</c>, drawn
    /// as an ordinary scene renderer the manager knows nothing about, by a camera of its own
    /// into a float target one texel high, a texel per channel. It reads the globals the
    /// manager publishes, as a world's shader would. See the DmxRead test shader.
    /// </summary>
    sealed class VRSLDmxReadProbe : System.IDisposable
    {
        public const string ShaderName = "Hidden/VRSL-URP/Tests/DmxRead";
        static readonly int s_Probe = Shader.PropertyToID("_Probe");

        public Camera        Camera   { get; private set; }
        public RenderTexture Target   { get; private set; }
        public Material      Material { get; private set; }
        readonly GameObject _root;
        readonly MeshFilter _filter;
        readonly Mesh       _triangle, _points;
        readonly int        _count, _layer, _rigMaskWas;
        readonly Camera     _rigCamera;

        VRSLDmxReadProbe(VRSLDMXRig rig, int count, int layer)
        {
            _count = count;
            _layer = layer;
            var shader = Shader.Find(ShaderName);
            Assert.IsNotNull(shader, $"the {ShaderName} test shader is not in this project");
            Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            _root = new GameObject("VRSL DMX read probe");
            var bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
            _triangle = new Mesh { name = "Probe triangle", vertices = new Vector3[3], bounds = bounds };
            _triangle.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, calculateBounds: false);
            _points = new Mesh { name = "Probe points", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                                 vertices = new Vector3[count], bounds = bounds };
            _points.SetIndices(Enumerable.Range(0, count).ToArray(), MeshTopology.Points, 0, calculateBounds: false);

            var drawn = new GameObject("Probe renderer");
            drawn.transform.SetParent(_root.transform, false);
            drawn.layer = layer;
            _filter = drawn.AddComponent<MeshFilter>();
            var renderer = drawn.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            Target = new RenderTexture(count, 1, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                     { name = "DMX read probe target", antiAliasing = 1 };
            Target.Create();
            var camGo = new GameObject("Probe camera");
            camGo.transform.SetParent(_root.transform, false);
            camGo.transform.position = new Vector3(0f, 0f, -10f);
            Camera = camGo.AddComponent<Camera>();
            Camera.clearFlags      = CameraClearFlags.SolidColor;
            Camera.backgroundColor = Color.clear;
            Camera.cullingMask     = 1 << layer;
            Camera.allowHDR        = true;
            Camera.allowMSAA       = false;
            Camera.targetTexture   = Target;
            Camera.enabled         = false;

            // The rig's own camera keeps its frame; this camera draws only the probe.
            _rigCamera  = rig.Camera;
            _rigMaskWas = _rigCamera.cullingMask;
            _rigCamera.cullingMask &= ~(1 << layer);
            // A camera into a texture is a mirror to the manager. Skip gives it no VRSL
            // lighting or beams, which would land on the probe's texels, and still the grid.
            rig.Manager.secondaryCameraMode = SecondaryCameraMode.Skip;
        }

        /// <summary>A probe for channels <c>first .. first + count - 1</c>, on a layer no
        /// renderer in the scene is on.</summary>
        public static VRSLDmxReadProbe Create(VRSLDMXRig rig, int count)
        {
            var occupied = new HashSet<int>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
                if (r != null) occupied.Add(r.gameObject.layer);
            for (int layer = 31; layer >= 8; layer--)
                if (!occupied.Contains(layer)) return new VRSLDmxReadProbe(rig, count, layer);
            Assert.Inconclusive("no layer is free for the probe");
            return null;
        }

        /// <summary>
        /// Render the probe and read it back: per texel, from channel <paramref name="first"/>,
        /// the channel, raw and strobe reads in r, g, b, or with <paramref name="colour"/> the
        /// colour of a fixture starting there. A texel nothing wrote reads -0.5.
        /// </summary>
        public Color[] Read(VRSLDMXRig rig, bool vertex, bool colour, int first)
        {
            _filter.sharedMesh = vertex ? _points : _triangle;
            Material.SetVector(s_Probe, new Vector4(colour ? 1f : 0f, vertex ? 1f : 0f, first, 0f));
            rig.Render(Camera);

            var readback = new Texture2D(_count, 1, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = Target;
                readback.ReadPixels(new Rect(0, 0, _count, 1), 0, 0, false);
                readback.Apply(false);
                return readback.GetPixels()
                               .Select(c => new Color((c.r - 0.25f) * 2f, (c.g - 0.25f) * 2f, (c.b - 0.25f) * 2f, c.a))
                               .ToArray();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(readback);
            }
        }

        public void Dispose()
        {
            if (_rigCamera != null) _rigCamera.cullingMask = _rigMaskWas;
            if (_root != null) Object.DestroyImmediate(_root);
            if (Target != null) { Target.Release(); Object.DestroyImmediate(Target); }
            Object.DestroyImmediate(_triangle);
            Object.DestroyImmediate(_points);
            Object.DestroyImmediate(Material);
        }
    }

    /// <summary>
    /// Reading DMX in a shader of your own, through the public include. Rows F1 to F5 of
    /// TESTING.md.
    /// </summary>
    class VRSLDmxReadTests : VRSLDMXTest
    {
        static readonly VRSLDmxInput[] Grids =
            { VRSLDmxInput.HorizontalGrid, VRSLDmxInput.VerticalGrid, VRSLDmxInput.LegacyGrid };

        static int PatchedChannels => VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 12;

        /// <summary>A raw grid for each layout, of another shape than the decode textures,
        /// so the raw read has to use the raw texture's own size to find its cells.</summary>
        static Vector2Int RawSizeOf(VRSLGridLayout layout) => layout switch
        {
            VRSLGridLayout.Vertical => new Vector2Int(26, 270),
            VRSLGridLayout.Legacy   => new Vector2Int(100, 100),
            _                       => new Vector2Int(26, 480),
        };

        /// <summary>The manager on <paramref name="grid"/>'s textures for every layout, set
        /// to <paramref name="input"/>, reading no channel source.</summary>
        static void UseGrid(VRSLDMXRig rig, VRSLGridTextures grid, VRSLDmxInput input)
        {
            rig.Manager.ChannelSource = null;
            rig.Manager.enabled       = false;
            rig.Manager.gridTextures  = grid;
            rig.Manager.dmxInput      = input;
            rig.Manager.enabled       = true;
        }

        static string Bytes(float v) => (v * 255f).ToString("F1");

        /// <summary>
        /// F1. The public include's four reads give what the package's fixtures read, on
        /// each grid layout and under Stream data, from the fragment and the vertex stage
        /// alike. On a grid the main, raw and strobe textures are painted with different
        /// values (the raw one at another size than the rest): the channel read gives the
        /// main grid's value, the raw read the raw grid's, the strobe read what the compute's
        /// strobe gives for that channel, and the colour read rgb (+7, +8, +9) times the
        /// dimmer (+5). Under Stream data the channel and raw reads give the byte the source
        /// sent.
        /// </summary>
        [UnityTest]
        public IEnumerator F1_a_shader_of_your_own_reads_what_the_fixtures_read(
            [Values(VRSLDmxInput.HorizontalGrid, VRSLDmxInput.VerticalGrid, VRSLDmxInput.LegacyGrid,
                    VRSLDmxInput.StreamData)] VRSLDmxInput input)
        {
            bool stream = input == VRSLDmxInput.StreamData;
            var rig = VRSLDMXRig.Build(withSource: stream);
            var painted = new List<VRSLDMXRig.PaintedGrid>();
            VRSLGridTextures grid = null;
            VRSLDmxReadProbe probe = null;
            try
            {
                yield return rig.Step(2);
                int count;
                float[] main, raw;
                var unreadable = new HashSet<int>();
                if (stream)
                {
                    rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                    rig.Source.speed     = 0f;
                    rig.Source.universes = 2;
                    rig.Manager.disableStrobe = false;
                    yield return rig.Step(3);
                    count = rig.Manager.ChannelCount;
                    Assert.Greater(count, 0, "the synthetic source is not publishing");
                    var flat = rig.Manager.PublishedChannels.ToArray();
                    main = flat.Take(count).Select(b => b / 255f).ToArray();
                    raw  = main;
                }
                else
                {
                    var layout = (VRSLGridLayout)input;
                    var size   = VRSLGridDecodeTests.SizeOf(layout);
                    var rawAt  = RawSizeOf(layout);
                    count = layout == VRSLGridLayout.Legacy ? PatchedChannels
                                                            : Mathf.Min(VRSLGridDecodeTests.Capacity(layout), 1536);
                    main = new float[count];
                    raw  = new float[count];
                    var strobe = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        main[i]   = (i % 251 + 2) / 255f;
                        raw[i]    = ((i * 7 + 100) % 251 + 2) / 255f;
                        strobe[i] = i % 3 == 0 ? 1f : 0f;
                    }
                    var m = VRSLDMXRig.PaintGrid(main,   VRSLDMXRig.OwnCellsOf(count, layout, size.x, size.y),   layout, size.x, size.y);
                    var r = VRSLDMXRig.PaintGrid(raw,    VRSLDMXRig.OwnCellsOf(count, layout, rawAt.x, rawAt.y), layout, rawAt.x, rawAt.y);
                    var s = VRSLDMXRig.PaintGrid(strobe, VRSLDMXRig.OwnCellsOf(count, layout, size.x, size.y),   layout, size.x, size.y);
                    painted.AddRange(new[] { m, r, s });
                    foreach (var p in painted) unreadable.UnionWith(p.Shared);
                    grid = ScriptableObject.CreateInstance<VRSLGridTextures>();
                    var set = new VRSLGridTextures.Set
                    {
                        main = m.Texture, raw = r.Texture, movement = m.Texture, strobe = s.Texture, spinTimer = m.Texture,
                    };
                    grid.horizontal = grid.vertical = grid.legacy = set;
                    UseGrid(rig, grid, input);
                    yield return rig.Step(3);
                    Assert.AreEqual(0, rig.Manager.ChannelCount, "the manager is still reading the channel buffer");
                    Assert.AreEqual((VRSLGridLayout?)layout, rig.Manager.GridLayout);
                    Assert.Greater(Enumerable.Range(0, count).Count(i => Mathf.Abs(main[i] - raw[i]) > Half), count / 2,
                        "the raw grid mostly matches the main one, so the raw half proves nothing");
                }

                probe = VRSLDmxReadProbe.Create(rig, count + 1);
                // Under Stream data the strobe runs on the clock, and its three rates are all
                // open together now and then: wait for a frame where some channels are shut.
                var computeStrobe = rig.ReadComputeReads(0, count + 1, 3);
                for (int f = 0; stream && f < 120 && computeStrobe.Skip(1).Distinct().Count() < 2; f++)
                {
                    yield return null;
                    rig.RenderFrame();
                    computeStrobe = rig.ReadComputeReads(0, count + 1, 3);
                }
                Assert.IsTrue(computeStrobe.Skip(1).Distinct().Count() > 1,
                    "every channel's strobe reads alike, so the strobe half proves nothing");

                var wrong = new List<string>();
                var stages = new Dictionary<bool, (Color[] reads, Color[] colour)>();
                foreach (bool vertex in new[] { false, true })
                {
                    var reads  = probe.Read(rig, vertex, colour: false, first: 0);
                    var colour = probe.Read(rig, vertex, colour: true,  first: 0);
                    stages[vertex] = (reads, colour);
                    string stage = vertex ? "vertex" : "fragment";
                    for (int ch = 1; ch <= count; ch++)
                    {
                        if (unreadable.Contains(ch)) continue;
                        var got = reads[ch];
                        if (Mathf.Abs(got.r - main[ch - 1]) > Half)
                            wrong.Add($"{stage} channel {ch}: {Bytes(got.r)}, painted {Bytes(main[ch - 1])}");
                        if (Mathf.Abs(got.g - raw[ch - 1]) > Half)
                            wrong.Add($"{stage} raw {ch}: {Bytes(got.g)}, painted {Bytes(raw[ch - 1])}");
                        if (Mathf.Abs(got.b - computeStrobe[ch]) > Half)
                            wrong.Add($"{stage} strobe {ch}: {got.b:F2}, the compute's {computeStrobe[ch]:F2}");
                    }
                    for (int ch = 1; ch + 9 <= count; ch++)
                    {
                        int[] uses = { ch + 5, ch + 7, ch + 8, ch + 9 };
                        if (uses.Any(unreadable.Contains)) continue;
                        float dimmer = main[ch + 4];
                        var want = new Vector3(main[ch + 6], main[ch + 7], main[ch + 8]) * dimmer;
                        var got  = colour[ch];
                        if (Mathf.Abs(got.r - want.x) > Half || Mathf.Abs(got.g - want.y) > Half || Mathf.Abs(got.b - want.z) > Half)
                            wrong.Add($"{stage} colour {ch}: ({Bytes(got.r)}, {Bytes(got.g)}, {Bytes(got.b)}), "
                                    + $"expected ({Bytes(want.x)}, {Bytes(want.y)}, {Bytes(want.z)})");
                    }
                }
                for (int ch = 1; ch <= count; ch++)
                {
                    Color f = stages[false].reads[ch], v = stages[true].reads[ch];
                    Color fc = stages[false].colour[ch], vc = stages[true].colour[ch];
                    if (Mathf.Abs(f.r - v.r) > Half || Mathf.Abs(f.g - v.g) > Half || Mathf.Abs(f.b - v.b) > Half
                     || Mathf.Abs(fc.r - vc.r) > Half || Mathf.Abs(fc.g - vc.g) > Half || Mathf.Abs(fc.b - vc.b) > Half)
                        wrong.Add($"channel {ch} reads differently in the vertex stage: {v} against {f}");
                }

                Debug.Log($"[F1] {input}: {count} channels, {unreadable.Count} sharing a texel; "
                        + (wrong.Count == 0 ? "every read matched" : $"{wrong.Count} wrong: " + string.Join("; ", wrong.Take(12))));
                Assert.IsEmpty(wrong, $"{input}: {wrong.Count} public reads differ: " + string.Join("; ", wrong.Take(24)));
            }
            finally
            {
                probe?.Dispose();
                rig.Dispose();
                foreach (var p in painted) p.Dispose();
                if (grid != null) Object.DestroyImmediate(grid);
            }
        }

        /// <summary>
        /// F2. On each grid, through the layout's real decode chain with its clock running, a
        /// channel stepped from one value to another on the video grid reads the new value
        /// through the raw read on the first frame after, while the smoothed read eases:
        /// short of the new value then, strictly between the two soon after, and meeting it
        /// later. Under Stream data, which has no colour smoothing, the two reads are the same
        /// every frame while the source sweeps.
        /// </summary>
        [UnityTest]
        public IEnumerator F2_the_raw_read_steps_where_the_smoothed_read_eases(
            [Values(VRSLDmxInput.HorizontalGrid, VRSLDmxInput.VerticalGrid, VRSLDmxInput.LegacyGrid,
                    VRSLDmxInput.StreamData)] VRSLDmxInput input)
        {
            const int Channel = 27;
            const float Tolerance = VRSLGridSwitchTests.VideoTolerance;
            if (input == VRSLDmxInput.StreamData)
            {
                yield return StreamRawEqualsSmoothed();
                yield break;
            }

            var layout = (VRSLGridLayout)input;
            var asset  = VRSLGridSwitchTests.Shipped();
            var modes  = VRSLGridSwitchTests.Snapshot(asset);
            var rig    = VRSLDMXRig.Build(withSource: false);
            VRSLDmxReadProbe probe = null;
            try
            {
                Assert.AreSame(asset, rig.Manager.gridTextures, "the rig's manager is not on the shipped Grid Textures");
                rig.Manager.SetDmxInput((int)input);
                Assert.AreEqual((VRSLGridLayout?)layout, rig.Manager.GridLayout);
                var set = asset.For(layout);
                VRSLDMXRig.ClearGridTextures(set);
                probe = VRSLDmxReadProbe.Create(rig, Channel + 1);
                yield return VRSLGridSwitchTests.PastTheChainsStartUp(rig);

                float before = 40f / 255f, after = 200f / 255f;
                var values = new float[Channel];
                for (int i = 0; i < Channel; i++) values[i] = before;
                VRSLDMXRig.PaintVideoGrid(set, layout, values);
                for (int f = 0; f < 300; f++)
                {
                    yield return null;
                    rig.RenderFrame();
                    var settled = probe.Read(rig, vertex: false, colour: false, first: 0)[Channel];
                    if (Mathf.Abs(settled.r - before) <= Tolerance && Mathf.Abs(settled.g - before) <= Tolerance) break;
                    if (f == 299) Assert.Fail($"the reads never settled on {Bytes(before)}: smoothed {Bytes(settled.r)}, raw {Bytes(settled.g)}");
                }

                values[Channel - 1] = after;
                VRSLDMXRig.PaintVideoGrid(set, layout, values);
                var trace = new List<Color>();
                for (int f = 0; f < 300; f++)
                {
                    yield return null;
                    rig.RenderFrame();
                    var read = probe.Read(rig, vertex: false, colour: false, first: 0)[Channel];
                    trace.Add(read);
                    if (f > 3 && Mathf.Abs(read.r - after) <= Tolerance) break;
                }
                string Trace() => string.Join(", ", trace.Take(8).Select(c => $"{Bytes(c.r)}/{Bytes(c.g)}"));
                Debug.Log($"[F2] {layout}: smoothed/raw after the step: {Trace()} ... met after {trace.Count} frames");

                Assert.AreEqual(after, trace[0].g, Tolerance,
                    $"the raw read doesn't have the new value on the first frame: smoothed/raw {Trace()}");
                Assert.Less(trace[0].r, after - Tolerance,
                    $"the smoothed read jumped straight to the new value: smoothed/raw {Trace()}");
                Assert.IsTrue(trace.Take(4).Any(c => c.r > before + Tolerance && c.r < after - Tolerance),
                    $"the smoothed read never sat between the two values: smoothed/raw {Trace()}");
                Assert.AreEqual(after, trace[trace.Count - 1].r, Tolerance,
                    $"the smoothed read never met the raw one in {trace.Count} frames");
                Assert.AreSame(set.main is CustomRenderTexture crt ? crt.material.GetTexture("_DMXTexture") : null, set.raw,
                    $"the shipped {layout} raw slot isn't the video grid its decode chain reads");
            }
            finally
            {
                probe?.Dispose();
                rig.Dispose();
                foreach (var l in VRSLGridSwitchTests.Layouts) VRSLDMXRig.ClearGridTextures(asset.For(l));
                VRSLGridSwitchTests.Restore(modes);
            }
        }

        static IEnumerator StreamRawEqualsSmoothed()
        {
            const int Channels = 64;
            var rig = VRSLDMXRig.Build();
            VRSLDmxReadProbe probe = null;
            try
            {
                rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Sweep;
                rig.Source.speed   = 1f;
                probe = VRSLDmxReadProbe.Create(rig, Channels + 1);
                var seen = new HashSet<float>();
                var wrong = new List<string>();
                for (int f = 0; f < 30; f++)
                {
                    yield return null;
                    rig.RenderFrame();
                    var reads = probe.Read(rig, vertex: false, colour: false, first: 0);
                    for (int ch = 1; ch <= Channels; ch++)
                    {
                        seen.Add(Mathf.Round(reads[ch].r * 255f));
                        if (Mathf.Abs(reads[ch].r - reads[ch].g) > Half)
                            wrong.Add($"frame {f} ch {ch}: smoothed {Bytes(reads[ch].r)}, raw {Bytes(reads[ch].g)}");
                    }
                }
                Debug.Log($"[F2] Stream data: {seen.Count} distinct values over 30 frames, {wrong.Count} frames apart");
                Assert.Greater(seen.Count, 3, "the source's values hardly moved, so the row proves nothing");
                Assert.IsEmpty(wrong, "under Stream data the raw and smoothed reads differ: " + string.Join("; ", wrong.Take(12)));
            }
            finally
            {
                probe?.Dispose();
                rig.Dispose();
            }
        }

        const string Shaders = "Packages/town.mr.vrsl-urp/Tests/Runtime/Shaders/";

        /// <summary>Every pass of <paramref name="shader"/>, vertex and fragment, compiled for
        /// a platform; the failures, with Unity's messages.</summary>
        static List<string> Compile(Shader shader, ShaderCompilerPlatform platform, BuildTarget target,
                                    string[] keywords, ref int compiled)
        {
            var failures = new List<string>();
            var sub = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            for (int p = 0; p < sub.PassCount; p++)
            {
                var pass = sub.GetPass(p);
                foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                {
                    var info = pass.CompileVariant(stage, keywords, platform, target);
                    compiled++;
                    var errors = info.Messages.Where(m => m.severity == ShaderCompilerMessageSeverity.Error)
                                              .Select(m => $"{m.message} ({m.file}:{m.line})").ToList();
                    if (info.Success && errors.Count == 0) continue;
                    failures.Add($"{shader.name} pass '{pass.Name}' {stage} for {platform} {target} [{string.Join(" ", keywords)}]: "
                               + (errors.Count > 0 ? string.Join("; ", errors) : "failed with no message"));
                }
            }
            return failures;
        }

        /// <summary>
        /// F3. A shader using the public include keeps its own SRP Batcher layout: the probe
        /// is compatible by Unity's own check once it has drawn. And the include sits beside
        /// URP's Lit inputs and its depth texture, either side of them, compiling on Windows
        /// and for Quest.
        /// </summary>
        [UnityTest]
        public IEnumerator F3_the_include_keeps_the_batcher_and_sits_beside_urps_own()
        {
            const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var util   = typeof(ShaderUtil);
            var code   = util.GetMethod("GetSRPBatcherCompatibilityCode", Any);
            var reason = util.GetMethod("GetSRPBatcherCompatibilityIssueReason", Any);
            var active = util.GetMethod("GetShaderActiveSubshaderIndex", Any);

            var beside = AssetDatabase.LoadAssetAtPath<Shader>(Shaders + "VRSLDmxReadBesideUrp.shader");
            Assert.IsNotNull(beside, "the DmxReadBesideUrp test shader is not in this project");
            int compiled = 0;
            var failures = Compile(beside, ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, new string[0], ref compiled)
                .Concat(Compile(beside, ShaderCompilerPlatform.Vulkan, BuildTarget.Android, new string[0], ref compiled)).ToList();
            Debug.Log($"[F3] {compiled} programs beside URP's includes, {failures.Count} failed");

            if (code == null || reason == null || active == null)
            {
                Assert.IsEmpty(failures, string.Join("\n", failures));
                Assert.Inconclusive("this editor has no ShaderUtil SRP Batcher check to ask");
            }
            var rig = VRSLDMXRig.Build(withSource: false);
            VRSLDmxReadProbe probe = null;
            int c = -1;
            try
            {
                probe = VRSLDmxReadProbe.Create(rig, 16);
                yield return rig.Step(2);
                probe.Read(rig, vertex: false, colour: false, first: 1);
                var shader = probe.Material.shader;
                int sub = (int)active.Invoke(null, new object[] { shader });
                c = (int)code.Invoke(null, new object[] { shader, sub });
                Debug.Log($"[F3] {shader.name}: SRP Batcher code {c}");
                if (c > 1)
                    failures.Add("the SRP Batcher refuses a shader using the include: "
                               + (string)reason.Invoke(null, new object[] { shader, sub, c }));
            }
            finally
            {
                probe?.Dispose();
                rig.Dispose();
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
            if (c == 1) Assert.Inconclusive("Unity's SRP Batcher check reads not initialized even after a draw");
        }

        /// <summary>
        /// F4. A world whose only DMX reader is its own shader still gets the grid: with the
        /// manager on a grid and no package fixtures in the scene, never any, or after the last
        /// one is switched off, the probe reads the painted grid rather than black.
        /// </summary>
        [UnityTest]
        public IEnumerator F4_a_shader_of_your_own_reads_the_grid_with_no_fixtures(
            [Values(false, true)] bool hadFixtures)
        {
            const int Count = 100;
            const VRSLGridLayout layout = VRSLGridLayout.Horizontal;
            var rig = VRSLDMXRig.Build(fixtures: hadFixtures ? VRSLDMXRig.FixtureCount : 0, withSource: false);
            VRSLDMXRig.PaintedGrid painted = null;
            VRSLGridTextures grid = null;
            VRSLDmxReadProbe probe = null;
            try
            {
                var size   = VRSLGridDecodeTests.SizeOf(layout);
                var values = Enumerable.Range(0, Count).Select(i => (i % 200 + 40) / 255f).ToArray();
                painted = VRSLDMXRig.PaintGrid(values, VRSLDMXRig.OwnCellsOf(Count, layout, size.x, size.y), layout, size.x, size.y);
                grid = ScriptableObject.CreateInstance<VRSLGridTextures>();
                var set = new VRSLGridTextures.Set { main = painted.Texture, raw = painted.Texture, movement = painted.Texture,
                                                     strobe = painted.Texture, spinTimer = painted.Texture };
                grid.horizontal = grid.vertical = grid.legacy = set;
                UseGrid(rig, grid, VRSLDmxInput.HorizontalGrid);
                probe = VRSLDmxReadProbe.Create(rig, Count + 1);
                yield return rig.Step(3);
                if (hadFixtures)
                {
                    Assert.Greater(rig.Manager.FixtureCount, 0, "the rig's fixtures were never collected");
                    foreach (var f in rig.Fixtures) f.enabled = false;
                    yield return rig.Step(3);
                }
                Assert.AreEqual(0, rig.Manager.FixtureCount, "the manager still holds fixtures");

                var reads = probe.Read(rig, vertex: false, colour: false, first: 0);
                var wrong = Enumerable.Range(1, Count).Where(ch => Mathf.Abs(reads[ch].r - values[ch - 1]) > Half)
                                      .Select(ch => $"ch {ch}: {Bytes(reads[ch].r)}, painted {Bytes(values[ch - 1])}").ToList();
                Debug.Log($"[F4] {(hadFixtures ? "after the last fixture switched off" : "never any fixtures")}: "
                        + (wrong.Count == 0 ? "the probe read the grid" : string.Join("; ", wrong.Take(8))));
                Assert.IsEmpty(wrong, $"{wrong.Count} channels read other than the grid: " + string.Join("; ", wrong.Take(12)));
            }
            finally
            {
                probe?.Dispose();
                rig.Dispose();
                painted?.Dispose();
                if (grid != null) Object.DestroyImmediate(grid);
            }
        }

        /// <summary>
        /// F5. The probe, which uses the include and nothing else of the package's, compiles
        /// vertex and fragment for Windows (D3D, serving D3D11 and D3D12), for Quest (Android
        /// Vulkan, plain and with multiview) and for desktop Linux (Vulkan).
        /// </summary>
        [Test]
        public void F5_the_include_compiles_on_every_platform_the_package_runs_on()
        {
            var shader = Shader.Find(VRSLDmxReadProbe.ShaderName);
            Assert.IsNotNull(shader, $"the {VRSLDmxReadProbe.ShaderName} test shader is not in this project");
            int compiled = 0;
            var failures = new List<string>();
            failures.AddRange(Compile(shader, ShaderCompilerPlatform.D3D,    BuildTarget.StandaloneWindows64, new string[0], ref compiled));
            failures.AddRange(Compile(shader, ShaderCompilerPlatform.Vulkan, BuildTarget.Android,             new string[0], ref compiled));
            failures.AddRange(Compile(shader, ShaderCompilerPlatform.Vulkan, BuildTarget.Android,             new[] { "STEREO_MULTIVIEW_ON" }, ref compiled));
            failures.AddRange(Compile(shader, ShaderCompilerPlatform.Vulkan, BuildTarget.StandaloneLinux64,   new string[0], ref compiled));
            Debug.Log($"[F5] {compiled} programs compiled, {failures.Count} failed");
            Assert.AreEqual(8, compiled, "the probe's vertex and fragment programs weren't all compiled");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }
    }
}
#endif
