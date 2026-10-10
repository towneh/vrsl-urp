#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The decode in each grid layout and from a channel source, and what the fixtures
    /// read from each. Rows G6 to G8, G10, G16, G17 and N29 to N33 of TESTING.md.
    /// </summary>
    class VRSLGridDecodeTests : VRSLDMXTest
    {
        static Vector3 Rgb(VRSL_URPLightManager.VRSLLightData d)
            => new Vector3(d.colorAndIntensity.x, d.colorAndIntensity.y, d.colorAndIntensity.z);

        /// <summary>The last channel the rig patches: its last fixture's thirteenth.</summary>
        static int PatchedChannels => VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 12;

        static RenderTexture Solid(Color colour)
        {
            var rt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                     { name = "Solid test grid" };
            rt.Create();
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, colour);
            RenderTexture.active = previous;
            return rt;
        }

        /// <summary>
        /// G6. On a Legacy grid carrying the channel buffer's own values, the
        /// compute decodes every channel the rig patches from the texel the shader-driven
        /// fixtures read it from, and so what the buffer holds for every channel the
        /// layout can carry; the realtime fixtures light as they do from the buffer.
        ///
        /// The texels come from the fixtures' include, found on the GPU, so the row
        /// compares the compute's Legacy read with theirs rather than with a second
        /// derivation. Some channels share a texel: a Legacy row holds two sectors, and
        /// on the 50 by 50 decode textures the first sector's 13th channel (u 0.50) and
        /// the second's 1st (u 0.52) are a texel apart on texel edges, which this GPU
        /// settles onto the same texel. That is the layout and the hardware, not the
        /// decode, and another GPU may settle it differently, so the row names no shared
        /// set: for a shared channel the claim is only that the compute reads what the
        /// fixtures read. The compute's 13th-channel correction table is the one known
        /// difference from the fixtures, in every layout (N24 finds it on real video):
        /// for those channels the compute reads the cell 13 below, and the row asserts
        /// exactly that, since it is also where a port goes almost right.
        /// </summary>
        [UnityTest]
        public IEnumerator G6_a_legacy_grid_decodes_what_the_channel_buffer_does()
        {
            var rig = VRSLDMXRig.Build();
            VRSLDMXRig.PaintedGrid painted = null;
            RenderTexture white = null;
            VRSLGridTextures grid = null;
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                // Strobe out of the comparison: the grid path takes it from the strobe
                // texture (white, open) and the buffer path from the channel.
                rig.Manager.disableStrobe = true;
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 2;
                yield return rig.Step(3);

                var viaBuffer = rig.ReadLights();
                var flat = rig.Manager.PublishedChannels.ToArray();
                Assert.GreaterOrEqual(flat.Length, PatchedChannels, "the source published too few channels");
                var values = new float[PatchedChannels];
                for (int i = 0; i < values.Length; i++) values[i] = flat[i] / 255f;

                // The Legacy decode textures are 50 by 50; the painted grid matches them.
                painted = VRSLDMXRig.PaintGrid(values, VRSLGridLayout.Legacy, 50, 50);
                white   = Solid(Color.white);
                grid    = ScriptableObject.CreateInstance<VRSLGridTextures>();
                grid.legacy = new VRSLGridTextures.Set
                {
                    main = painted.Texture, movement = painted.Texture, strobe = white,
                    spinTimer = painted.Texture,
                };
                Debug.Log($"[G6] {painted.Shared.Count} channels share a texel in the Legacy "
                        + "layout on this GPU: "
                        + string.Join(", ", painted.Shared.Select(ch => $"{ch}@{painted.Cells[ch - 1]}")));

                rig.Manager.ChannelSource = null;
                rig.Manager.gridTextures  = grid;
                rig.Manager.dmxInput      = VRSLDmxInput.LegacyGrid;
                yield return rig.Step(3);
                Assert.AreEqual(0, rig.Manager.ChannelCount, "the manager is still reading the channel buffer");

                var read      = rig.ReadGridChannels(PatchedChannels);
                var notAsRead = new List<string>();
                var notAsSent = new List<string>();
                for (int i = 0; i < read.Length; i++)
                {
                    // The compute's correction table reads these from the cell 13 below,
                    // as N24 finds on real video; the fixtures read their own.
                    int from = VRSLDMXRig.ShiftedThirteenth(i + 1) ? i - 13 : i;
                    if (Mathf.Abs(read[i] - painted.Reads[from]) > Half)
                        notAsRead.Add($"ch {i + 1} decoded {read[i] * 255f:F0}, the fixtures read "
                                    + $"{painted.Reads[from] * 255f:F0} for ch {from + 1} from texel {painted.Cells[from]}");
                    if (from == i && !painted.Shared.Contains(i + 1) && Mathf.Abs(read[i] - values[i]) > Half)
                        notAsSent.Add($"ch {i + 1} decoded {read[i] * 255f:F0}, sent {values[i] * 255f:F0}");
                }
                Assert.IsEmpty(notAsRead, $"{notAsRead.Count} channels decode from a different texel "
                                        + "than the fixtures read: " + string.Join("; ", notAsRead));
                Assert.IsEmpty(notAsSent, $"{notAsSent.Count} channels decode wrong from a Legacy grid: "
                                        + string.Join("; ", notAsSent));

                var viaGrid = rig.ReadLights();
                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                {
                    AssertNear(Rgb(viaBuffer[i]), Rgb(viaGrid[i]),
                        $"fixture {i} lights a different colour from a Legacy grid than from the buffer");
                    Assert.AreEqual(viaBuffer[i].colorAndIntensity.w, viaGrid[i].colorAndIntensity.w, 0.01f,
                        $"fixture {i} lights at a different intensity from a Legacy grid than from the buffer");
                }
            }
            finally
            {
                rig.Dispose();
                painted?.Dispose();
                if (white   != null) Object.DestroyImmediate(white);
                if (grid    != null) Object.DestroyImmediate(grid);
            }
        }

        /// <summary>
        /// G7. With a channel source publishing, a manager with no grid textures
        /// at all lights its realtime fixtures: the buffer path reads no grid, so it must
        /// not need one.
        /// </summary>
        [UnityTest]
        public IEnumerator G7_a_channel_source_lights_the_rig_with_no_grid()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                // Brought up again with nothing wired, which is what a player does with
                // a scene saved that way; in the editor the wiring would fill it back in.
                using (VRSLWiring.SuppressAutoResolve(rig.Manager))
                {
                    rig.Manager.enabled      = false;
                    rig.Manager.gridTextures = null;
                    rig.Manager.enabled      = true;
                }
                rig.Manager.ChannelSource = rig.Source;
                Assert.IsNull(rig.Manager.LiveTextures.main, "the manager still has a grid");

                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 4;
                yield return rig.Step(4);
                // Fails here if nothing decoded: Calibrate needs the light data written.
                rig.Calibrate();

                var lights = rig.ReadLights();
                for (int i = 0; i <= 38; i++)
                {
                    int red = VRSLDMXRig.RedChannel(VRSLDMXRig.ChannelOf(i));
                    var expected = new Vector3(VRSLDMXRig.RampAt(red), VRSLDMXRig.RampAt(red + 1),
                                               VRSLDMXRig.RampAt(red + 2));
                    AssertNear(expected, Rgb(lights[i]), $"fixture {i} did not light from the buffer with no grid");
                }
            }
            finally { rig.Dispose(); }
        }


        // ── The fixtures follow the manager's layout ──────────────────────────

        const string Prefabs = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/";

        /// <summary>One of each Horizontal fixture whose body or beam reads the grid.</summary>
        static readonly string[] HorizontalPrefabs =
        {
            "Horizontal Mode/VRSL-DMX-Static-Flasher-H-1CH.prefab",
            "Horizontal Mode/VRSL-DMX-Static-Laser-H-13CH.prefab",
            "Horizontal Mode/VRSL-DMX-Static-LightBar-H-13CH.prefab",
            "Horizontal Mode/VRSL-DMX-Static-MultiLightBar-H-15CH.prefab",
            "Horizontal Mode/5-Channel Statics/VRSL-DMX-Static-6x4StrobeLight-H-5CH.prefab",
            "Horizontal Mode/VRSL-DMX-Static-DiscoBall-H-1CH.prefab",
            "Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Mover-Spotlight-H-13CH-URP.prefab",
            "Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Mover-WashLight-H-13CH-URP.prefab",
            "Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-Blinder-H-13CH-URP.prefab",
            "Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-ParLight-H-13CH-URP.prefab",
        };

        /// <summary>The Legacy fixture prefabs 0.2.0-rc.6 shipped.</summary>
        static readonly string[] LegacyPrefabs =
        {
            "Legacy Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Mover-Spotlight-L-13CH-URP.prefab",
            "Legacy Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Mover-WashLight-L-13CH-URP.prefab",
            "Legacy Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-Blinder-L-13CH-URP.prefab",
            "Legacy Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-ParLight-L-13CH-URP.prefab",
        };

        /// <summary>The Vertical fixture prefabs 0.2.0-rc.6 shipped.</summary>
        static readonly string[] VerticalPrefabs =
        {
            "Vertical Mode/VRSL-DMX-Static-Flasher-V-1CH.prefab",
            "Vertical Mode/VRSL-DMX-Static-Laser-V-13CH.prefab",
            "Vertical Mode/VRSL-DMX-Static-LightBar-V-13CH.prefab",
            "Vertical Mode/VRSL-DMX-Static-MultiLightBar-V-15CH.prefab",
            "Vertical Mode/5-Channel Statics/VRSL-DMX-Static-6x4StrobeLight-V-5CH.prefab",
            "Vertical Mode/VRSL-DMX-Static-DiscoBall-V-1CH.prefab",
            "Vertical Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Mover-Spotlight-V-13CH-URP.prefab",
            "Vertical Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Mover-WashLight-V-13CH-URP.prefab",
            "Vertical Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-Blinder-V-13CH-URP.prefab",
            "Vertical Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Static-ParLight-V-13CH-URP.prefab",
        };

        /// <summary>Each layout's decode textures are this size, so each is painted at it.</summary>
        static Vector2Int SizeOf(VRSLGridLayout layout) => layout switch
        {
            VRSLGridLayout.Vertical => new Vector2Int(26, 135),
            VRSLGridLayout.Legacy   => new Vector2Int(50, 50),
            _                       => new Vector2Int(26, 240),
        };

        /// <summary>
        /// Channel values for the image rows: bright and uneven, so a fixture reading
        /// the wrong cell looks different. Channels that share a texel in any layout
        /// get one value between them, so the frames compare the read rather than what
        /// a layout can carry (see G6).
        /// </summary>
        static float[] ImageValues(int count)
        {
            var values = new float[count];
            for (int i = 0; i < count; i++)
                values[i] = 0.3f + 0.7f * Mathf.Repeat((i + 1) * 0.6180339f, 1f);

            foreach (VRSLGridLayout layout in System.Enum.GetValues(typeof(VRSLGridLayout)))
            {
                var size  = SizeOf(layout);
                var cells = VRSLDMXRig.CellsOf(count, layout, size.x, size.y);
                var first = new Dictionary<Vector2Int, int>();
                for (int i = 0; i < count; i++)
                {
                    if (first.TryGetValue(cells[i], out int owner)) values[i] = values[owner];
                    else first[cells[i]] = i;
                }
            }
            return values;
        }

        /// <summary>
        /// The rig's realtime movers and <paramref name="prefabs"/> in a row, lit from
        /// <paramref name="values"/> painted in <paramref name="layout"/> and handed to
        /// the manager, which is set to that layout. Captured from a camera in front of
        /// the prefabs. With <paramref name="switchAtRuntime"/> the manager comes up on
        /// Horizontal, renders a few frames reading the grid wrongly, and is switched by
        /// <c>SetDmxInput</c> instead. The prefabs are patched at sectors 2 onwards, or
        /// <paramref name="sectorOffset"/> further on.
        /// </summary>
        static IEnumerator CaptureLayout(string[] prefabs, VRSLGridLayout layout, float[] values,
                                         System.Action<Texture2D> onCaptured, bool switchAtRuntime = false,
                                         int sectorOffset = 0)
        {
            using var rig = VRSLDMXRig.Build();
            var painted = new List<VRSLDMXRig.PaintedGrid>();
            var stilled = new List<Material>();
            RenderTexture white = null;
            VRSLGridTextures grid = null;
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + prefabs[i]);
                    Assert.IsNotNull(prefab, $"{prefabs[i]} is not in the package");
                    var go = Object.Instantiate(prefab, rig.Floor.transform.parent);
                    go.transform.localPosition = new Vector3(2f + i * 1.6f, 1.5f, -6f);
                    go.transform.localRotation = Quaternion.identity;
                    // The Built-in discoball turns its dots with the shader clock, which no
                    // capture holds, so two captures of one scene differ wherever they land.
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!r.sharedMaterials.Any(m => m != null && m.shader.name.Contains("Discoball"))) continue;
                        foreach (var m in r.materials)
                        {
                            stilled.Add(m);
                            if (m.HasProperty("_RotationSpeed")) m.SetFloat("_RotationSpeed", 0f);
                        }
                    }
                    foreach (var s in go.GetComponentsInChildren<VRStageLighting_DMX_Static>(true))
                    {
                        s.useLegacySectorMode = true;
                        // Applied by the component's own Start, next frame.
                        s.sector = 2 + i + sectorOffset;
                    }
                    foreach (var f in go.GetComponentsInChildren<VRStageLighting_DMX_RealtimeLight>(true))
                    {
                        f.useLegacySectorMode = true;
                        f.sector = 2 + i + sectorOffset;
                        f.DriveFixtureShells();
                    }
                }

                var size = SizeOf(layout);
                var main = VRSLDMXRig.PaintGrid(values, layout, size.x, size.y);
                painted.Add(main);
                white = Solid(Color.white);
                grid  = ScriptableObject.CreateInstance<VRSLGridTextures>();
                var set = new VRSLGridTextures.Set
                {
                    main = main.Texture, movement = main.Texture, strobe = white, spinTimer = main.Texture,
                };
                // The same set in every slot: the layout the manager is set to is the
                // only thing that differs between captures.
                grid.horizontal = grid.vertical = grid.legacy = set;

                rig.Manager.ChannelSource = null;
                rig.Manager.enabled      = false;
                rig.Manager.gridTextures = grid;
                rig.Manager.dmxInput     = (VRSLDmxInput)(switchAtRuntime ? VRSLGridLayout.Horizontal : layout);
                rig.Manager.enabled      = true;
                rig.Manager.RefreshFixtures();
                rig.FreezeForImageCapture();
                if (switchAtRuntime)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        yield return null;
                        rig.RenderFrame();
                    }
                    rig.Manager.SetDmxInput((int)layout);
                }

                // Far enough back for all ten prefabs, at a size where their bodies are
                // more than a few pixels.
                var view = rig.AddSecondaryCamera(targetSize: 512,
                                                  eye: new Vector3(9.2f, 3f, -19f),
                                                  lookAt: new Vector3(9.2f, 1.5f, -6f));
                VRSLCameraFilter.RegisterMainView(view.Camera);

                for (int i = 0; i < 12; i++)
                {
                    yield return null;
                    rig.RenderFrame();
                }
                onCaptured(VRSLImageCompare.Read(view.Target));
            }
            finally
            {
                foreach (var p in painted) p.Dispose();
                foreach (var m in stilled) if (m != null) Object.DestroyImmediate(m);
                if (white != null) Object.DestroyImmediate(white);
                if (grid  != null) Object.DestroyImmediate(grid);
            }
        }

        static float LitShare(Texture2D frame)
        {
            var pixels = frame.GetPixels32();
            int lit = 0;
            foreach (var p in pixels) if (Mathf.Max(p.r, Mathf.Max(p.g, p.b)) > 12) lit++;
            return (float)lit / pixels.Length;
        }

        /// <summary>
        /// G8, without the switch. Every Horizontal fixture prefab renders the
        /// same on a Legacy or a Vertical grid as on a Horizontal one carrying the same
        /// channel values, once the manager is set to that layout: shader-driven bodies
        /// and beams through both includes, realtime bodies, and the realtime light.
        /// The Vertical case is there for the Built-in include, which takes its texel
        /// size from Unity's automatic value for the grid texture rather than from the
        /// manager.
        /// </summary>
        [UnityTest]
        public IEnumerator G8_a_horizontal_prefab_lights_the_same_on_every_layout()
        {
            var values = ImageValues(VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 25);
            Texture2D horizontal = null, other = null;
            try
            {
                yield return CaptureLayout(HorizontalPrefabs, VRSLGridLayout.Horizontal, values, t => horizontal = t);
                Assert.Greater(LitShare(horizontal), 0.01f,
                    "the Horizontal frame is nearly black, so the comparison would prove nothing");

                foreach (var layout in new[] { VRSLGridLayout.Legacy, VRSLGridLayout.Vertical })
                foreach (bool switched in new[] { false, true })
                {
                    string how = switched ? "switched to at runtime" : "set before the manager came up";
                    yield return CaptureLayout(HorizontalPrefabs, layout, values, t => other = t, switched);
                    var result = VRSLImageCompare.Compare(horizontal, other);
                    Debug.Log($"[G8] {layout}, {how}, against Horizontal: {result}");
                    if (result.DifferingPercent > EdgeBudget)
                        VRSLImageCompare.WriteImages(EvidenceFolder, $"G8-{layout}{(switched ? "-switched" : "")}",
                                                     horizontal, other);
                    Assert.LessOrEqual(result.DifferingPercent, EdgeBudget,
                        $"the fixtures render differently on a {layout} grid carrying the same "
                      + $"values, {how} ({result}). Images in {EvidenceFolder}");
                    Object.DestroyImmediate(other);
                    other = null;
                }
            }
            finally
            {
                if (horizontal != null) Object.DestroyImmediate(horizontal);
                if (other      != null) Object.DestroyImmediate(other);
            }
        }

        /// <summary>
        /// G10. The Legacy and Vertical fixture prefabs 0.2.0-rc.6 shipped, beside a manager
        /// in their layout, render the same from run to run, taking the layout from the
        /// manager's global. Strobe held. Against this machine's own previous capture, like
        /// I1, so the first run seeds and the row after that guards against change.
        /// </summary>
        [UnityTest]
        public IEnumerator G10_the_legacy_and_vertical_prefabs_render_as_before(
            [Values(VRSLGridLayout.Legacy, VRSLGridLayout.Vertical)] VRSLGridLayout layout)
        {
            var values = ImageValues(VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 25);
            var prefabs = layout == VRSLGridLayout.Legacy ? LegacyPrefabs : VerticalPrefabs;
            Texture2D frame = null, previous = null;
            try
            {
                yield return CaptureLayout(prefabs, layout, values, t => frame = t);
                Assert.Greater(LitShare(frame), 0.01f, "the frame is nearly black, so the comparison would prove nothing");

                string path = VRSLImageCompare.LocalPath(VRSLDMXRig.CaptureName($"{layout}-prefabs"));
                previous = VRSLImageCompare.Load(path);
                if (previous == null)
                {
                    VRSLImageCompare.Save(path, frame);
                    Assert.Inconclusive($"no previous capture on this machine, so this run seeded one at {path}.");
                }

                var result = VRSLImageCompare.Compare(previous, frame);
                Debug.Log($"[G10] {layout} prefabs against the previous capture: {result}");
                if (result.DifferingPercent > EdgeBudget)
                    VRSLImageCompare.WriteImages(EvidenceFolder, $"G10-{layout}", previous, frame);
                Assert.LessOrEqual(result.DifferingPercent, EdgeBudget,
                    $"the {layout} prefabs render differently from the previous capture ({result}). "
                  + $"If that was intended, delete {path} to re-seed. Images in {EvidenceFolder}");
            }
            finally
            {
                if (frame    != null) Object.DestroyImmediate(frame);
                if (previous != null) Object.DestroyImmediate(previous);
            }
        }

        /// <summary>
        /// G16. Every Horizontal fixture prefab renders the same patched at sectors 106 to
        /// 115, near the end of the grid's third universe, as at sectors 2 to 11, with the
        /// same values painted at both. The lasers, lens flares, discoballs and mover bodies
        /// read through the Built-in include, which sizes the grid from Unity's automatic
        /// texel size for the grid texture rather than from the manager; a wrong size moves
        /// the read further from its cell the further down the grid a fixture sits.
        /// </summary>
        [UnityTest]
        public IEnumerator G16_a_prefab_lights_the_same_at_the_end_of_the_grid()
        {
            const int offset = 104;
            var size  = SizeOf(VRSLGridLayout.Horizontal);
            int count = size.y / 2 * 13;
            var near  = ImageValues(VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 25);
            var far   = new float[count];
            for (int i = 0; i < far.Length; i++) far[i] = i < near.Length ? near[i] : 0.3f;
            // The prefabs read at most 15 channels from their own sector's first.
            int first = VRSLDMXRig.ChannelOf(2) - 1, last = VRSLDMXRig.ChannelOf(2 + HorizontalPrefabs.Length) + 15;
            for (int i = first; i < last; i++) far[i + offset * 13] = near[i];

            using (var painted = VRSLDMXRig.PaintGrid(far, VRSLGridLayout.Horizontal, size.x, size.y))
            {
                Debug.Log($"[G16] {painted.Shared.Count} channels share a texel on the Horizontal grid: "
                        + string.Join(", ", painted.Shared.Select(ch => $"{ch}@{painted.Cells[ch - 1]}")));
                var unreadable = Enumerable.Range(first, last - first)
                                           .Where(i => Mathf.Abs(painted.Reads[i + offset * 13] - near[i]) > Half)
                                           .Select(i => i + offset * 13 + 1).ToList();
                Assert.IsEmpty(unreadable, "the far sectors do not carry the near sectors' values on channels "
                                         + string.Join(", ", unreadable));
            }

            Texture2D nearFrame = null, farFrame = null;
            try
            {
                yield return CaptureLayout(HorizontalPrefabs, VRSLGridLayout.Horizontal, near, t => nearFrame = t);
                Assert.Greater(LitShare(nearFrame), 0.01f,
                    "the frame at the start of the grid is nearly black, so the comparison would prove nothing");
                yield return CaptureLayout(HorizontalPrefabs, VRSLGridLayout.Horizontal, far, t => farFrame = t,
                                           sectorOffset: offset);

                var result = VRSLImageCompare.Compare(nearFrame, farFrame);
                Debug.Log($"[G16] sectors {2 + offset}-{1 + offset + HorizontalPrefabs.Length} against "
                        + $"2-{1 + HorizontalPrefabs.Length}: {result}");
                if (result.DifferingPercent > EdgeBudget)
                    VRSLImageCompare.WriteImages(EvidenceFolder, "G16", nearFrame, farFrame);
                Assert.LessOrEqual(result.DifferingPercent, EdgeBudget,
                    $"the fixtures render differently at the end of the grid carrying the same values "
                  + $"({result}). Images in {EvidenceFolder}");
            }
            finally
            {
                if (nearFrame != null) Object.DestroyImmediate(nearFrame);
                if (farFrame  != null) Object.DestroyImmediate(farFrame);
            }
        }

        /// <summary>I1's allowance for edge scatter, which is all a correct frame may move.</summary>
        const float EdgeBudget = 0.25f;

        static string EvidenceFolder =>
            System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath)!.FullName,
                                   "VRSL-Benchmarks", "image-failures");
    }
}
#endif
