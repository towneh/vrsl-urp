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
    /// read from each. Rows G6 to G8, G16 to G22 and N29 to N33 of TESTING.md.
    /// </summary>
    class VRSLGridDecodeTests : VRSLDMXTest
    {
        static Vector3 Rgb(VRSL_DMXURPLightManager.VRSLLightData d)
            => new Vector3(d.colorAndIntensity.x, d.colorAndIntensity.y, d.colorAndIntensity.z);

        /// <summary>The last channel the rig patches: its last fixture's thirteenth.</summary>
        static int PatchedChannels => VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 12;

        internal static RenderTexture Solid(Color colour)
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
        /// fixtures read.
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
                    if (Mathf.Abs(read[i] - painted.Reads[i]) > Half)
                        notAsRead.Add($"ch {i + 1} decoded {read[i] * 255f:F0}, the fixtures read "
                                    + $"{painted.Reads[i] * 255f:F0} from texel {painted.Cells[i]}");
                    if (!painted.Shared.Contains(i + 1) && Mathf.Abs(read[i] - values[i]) > Half)
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

        /// <summary>One of each fixture prefab whose body or beam reads the grid.</summary>
        static readonly string[] HorizontalPrefabs =
        {
            "VRSL-DMX-Static-Flasher-1CH.prefab",
            "VRSL-DMX-Static-Laser-13CH.prefab",
            "VRSL-DMX-Static-LightBar-13CH.prefab",
            "VRSL-DMX-Static-MultiLightBar-15CH.prefab",
            "VRSL-DMX-Static-6x4StrobeLight-5CH.prefab",
            "VRSL-DMX-Mover-Spotlight-13CH.prefab",
            "VRSL-DMX-Mover-WashLight-13CH.prefab",
            "VRSL-DMX-Static-Blinder-13CH.prefab",
            "VRSL-DMX-Static-ParLight-13CH.prefab",
        };

        /// <summary>Each layout's decode textures are this size, so each is painted at it.</summary>
        internal static Vector2Int SizeOf(VRSLGridLayout layout) => layout switch
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
        internal static float[] ImageValues(int count)
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
        /// <paramref name="startOn"/>, renders a few frames reading the wrong thing, and is switched by
        /// <c>SetDmxInput</c> instead. The prefabs are patched at sectors 2 onwards, or
        /// <paramref name="sectorOffset"/> further on. With <paramref name="source"/> the
        /// synthetic source publishes that pattern, held still, over two universes beside
        /// the grid, and <paramref name="onReady"/> sees the rig just before the capture.
        /// The manager reads <paramref name="input"/>, or Stream data with a source and the
        /// layout without one.
        /// <paramref name="facing"/> turns each prefab, upright when it is not given. The
        /// camera sits at <paramref name="eye"/>, or far enough back for ten prefabs.
        /// <paramref name="lateChange"/> runs two frames before the capture, so the capture
        /// is the first frame rendered after the manager's next update.
        /// </summary>
        internal static IEnumerator CaptureLayout(string[] prefabs, VRSLGridLayout layout, float[] values,
                                         System.Action<Texture2D> onCaptured, bool switchAtRuntime = false,
                                         int sectorOffset = 0,
                                         VRSL_SyntheticDMXChannelSource.Pattern? source = null,
                                         System.Action<VRSLDMXRig> onReady = null,
                                         Vector3[] facing = null, Vector3? eye = null,
                                         System.Action<VRSLDMXRig> lateChange = null,
                                         VRSLDmxInput? input = null,
                                         VRSLDmxInput startOn = VRSLDmxInput.HorizontalGrid)
        {
            var chosen = input ?? (source.HasValue ? VRSLDmxInput.StreamData : (VRSLDmxInput)layout);
            using var rig = VRSLDMXRig.Build();
            var painted = new List<VRSLDMXRig.PaintedGrid>();
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
                    go.transform.localRotation = facing == null ? Quaternion.identity : Quaternion.Euler(facing[i]);
                    foreach (var s in go.GetComponentsInChildren<VRSLDMXStatic>(true))
                    {
                        s.useLegacySectorMode = true;
                        // Applied by the component's own Start, next frame.
                        s.sector = 2 + i + sectorOffset;
                    }
                    foreach (var f in go.GetComponentsInChildren<VRSLDMXRealtimeLight>(true))
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
                rig.Manager.dmxInput     = switchAtRuntime ? startOn : chosen;
                rig.Manager.enabled      = true;
                if (source.HasValue)
                {
                    rig.Source.pattern        = source.Value;
                    rig.Source.speed          = 0f;
                    rig.Source.universes      = 2;
                    rig.Manager.ChannelSource = rig.Source;
                }
                rig.Manager.RefreshFixtures();
                rig.FreezeForImageCapture();
                if (switchAtRuntime)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        yield return null;
                        rig.RenderFrame();
                    }
                    rig.Manager.SetDmxInput((int)chosen);
                }

                // Far enough back for all ten prefabs, at a size where their bodies are
                // more than a few pixels.
                var at   = eye ?? new Vector3(9.2f, 3f, -19f);
                var view = rig.AddSecondaryCamera(targetSize: 512, eye: at,
                                                  lookAt: new Vector3(at.x, 1.5f, -6f));
                VRSLCameraFilter.RegisterMainView(view.Camera);

                for (int i = 0; i < 12; i++)
                {
                    if (i == 10) lateChange?.Invoke(rig);
                    yield return null;
                    rig.RenderFrame();
                }
                onReady?.Invoke(rig);
                onCaptured(VRSLImageCompare.Read(view.Target));
            }
            finally
            {
                foreach (var p in painted) p.Dispose();
                if (white != null) Object.DestroyImmediate(white);
                if (grid  != null) Object.DestroyImmediate(grid);
            }
        }

        internal static float LitShare(Texture2D frame)
        {
            var pixels = frame.GetPixels32();
            int lit = 0;
            foreach (var p in pixels) if (Mathf.Max(p.r, Mathf.Max(p.g, p.b)) > 12) lit++;
            return (float)lit / pixels.Length;
        }

        /// <summary>
        /// G8, without the switch. Every fixture prefab whose body or beam reads the grid
        /// renders the same on a Legacy or a Vertical grid as on a Horizontal one carrying
        /// the same channel values, once the manager is set to that layout: shader-driven
        /// bodies and beams through both includes, realtime bodies, and the realtime light.
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
        /// G16. G8's nine prefabs render the same patched at sectors 106 to 114, near the
        /// end of the Horizontal grid's third universe, as at sectors 2 to 10, with the
        /// same values painted at both. Both includes size the grid from the manager's
        /// <c>_VRSLDMXTexelSize</c>; a wrong size moves the read further from its cell the
        /// further down the grid a fixture sits.
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

        // ── The fixtures follow a channel source ──────────────────────────────

        /// <summary>The prefabs whose bodies read the grid through the URP include.</summary>
        internal static readonly string[] UrpIncludePrefabs =
        {
            "VRSL-DMX-Static-Flasher-1CH.prefab",
            "VRSL-DMX-Static-LightBar-13CH.prefab",
            "VRSL-DMX-Static-MultiLightBar-15CH.prefab",
            "VRSL-DMX-Static-6x4StrobeLight-5CH.prefab",
            "VRSL-DMX-Static-Blinder-13CH.prefab",
            "VRSL-DMX-Static-ParLight-13CH.prefab",
        };

        /// <summary>
        /// Each of <see cref="UrpIncludePrefabs"/> turned so the side it glows from faces
        /// the capture. Upright, the flasher, the bar and the strobe glow at the floor and
        /// the 15-channel bar away from the camera, so a frame of them upright shows their
        /// bodies black whatever they read.
        /// </summary>
        internal static readonly Vector3[] UrpIncludeFacing =
        {
            new(90f, 0f, 0f), new(90f, 0f, 0f), new(0f, 180f, 0f), new(90f, 0f, 0f), Vector3.zero, Vector3.zero,
        };

        /// <summary>The prefabs that move: the laser, whose beams read the grid
        /// through the Built-in include, and both realtime movers, whose heads read it through
        /// the URP one.</summary>
        internal static readonly string[] MovingPrefabs =
        {
            "VRSL-DMX-Static-Laser-13CH.prefab",
            "VRSL-DMX-Mover-Spotlight-13CH.prefab",
            "VRSL-DMX-Mover-WashLight-13CH.prefab",
        };

        /// <summary>
        /// What N29 and N30 allow to differ. The frames are the same scene lit from the
        /// same values, so the allowance is for a stray pixel; a fixture reading the wrong
        /// thing moves whole bodies.
        /// </summary>
        internal const float SameValuesBudget = 0.01f;

        /// <summary>
        /// <paramref name="prefabs"/> and the rig lit from the synthetic source's still
        /// Fixtures pattern, with a grid carrying other values bound beside it, against the
        /// same scene lit from a grid carrying what the source published and no source.
        /// Strobe held. The camera sits at <paramref name="eye"/>, close enough that the
        /// bodies fill more than a sliver of the frame.
        /// </summary>
        static IEnumerator CompareSourceWithGrid(string row, string[] prefabs, Vector3[] facing, Vector3 eye)
        {
            int count = VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 25;
            // Anything but what the source sends: a fixture still reading the grid lights
            // from these.
            var other     = ImageValues(count);
            var published = new float[count];

            Texture2D fromGrid = null, fromSource = null;
            try
            {
                yield return CaptureLayout(prefabs, VRSLGridLayout.Horizontal, other, t => fromSource = t,
                                           source: VRSL_SyntheticDMXChannelSource.Pattern.Fixtures,
                                           onReady: rig =>
                                           {
                                               var flat = rig.Manager.PublishedChannels.ToArray();
                                               for (int i = 0; i < count; i++) published[i] = flat[i] / 255f;
                                           },
                                           facing: facing, eye: eye);
                Assert.IsTrue(published.Any(v => v > 0f), "the source published nothing");
                yield return CaptureLayout(prefabs, VRSLGridLayout.Horizontal, published, t => fromGrid = t,
                                           facing: facing, eye: eye);
                Assert.Greater(LitShare(fromGrid), 0.01f,
                    "the frame lit from the grid is nearly black, so the comparison would prove nothing");

                var result = VRSLImageCompare.Compare(fromGrid, fromSource);
                Debug.Log($"[{row}] lit from the source against lit from a grid carrying the same values: {result}");
                if (result.DifferingPercent > SameValuesBudget)
                    VRSLImageCompare.WriteImages(EvidenceFolder, row, fromGrid, fromSource);
                Assert.LessOrEqual(result.DifferingPercent, SameValuesBudget,
                    $"the fixtures render differently from a channel source than from a grid carrying the "
                  + $"same values ({result}). Images in {EvidenceFolder}");
            }
            finally
            {
                if (fromGrid   != null) Object.DestroyImmediate(fromGrid);
                if (fromSource != null) Object.DestroyImmediate(fromSource);
            }
        }

        /// <summary>
        /// N29. The flasher, light bar, 15-channel bar, 6x4 strobe and realtime
        /// blinder and par, with the rig's movers behind them, light the same from a channel
        /// source as from a grid carrying what it published: bodies and mover heads on the URP
        /// include, lens flares on the Built-in one, and the realtime light.
        /// </summary>
        [UnityTest]
        public IEnumerator N29_shader_driven_fixtures_follow_a_channel_source()
            => CompareSourceWithGrid("N29", UrpIncludePrefabs, UrpIncludeFacing, new Vector3(6f, 2f, -15f));

        /// <summary>
        /// N30, N29 for what moves. The laser and both realtime movers light the same from a
        /// channel source as from a grid carrying what it published: the laser's beams,
        /// movement and spin, and the mover heads, which pan and tilt with their beams.
        /// </summary>
        [UnityTest]
        public IEnumerator N30_lasers_and_mover_heads_follow_a_channel_source()
            => CompareSourceWithGrid("N30", MovingPrefabs, new Vector3[MovingPrefabs.Length],
                                     new Vector3(4.4f, 2f, -12f));

        /// <summary>
        /// Channels 1..<paramref name="count"/> through one include's read functions, drawn
        /// by <paramref name="probe"/>: the value in red, the movement value in green and
        /// the spin phase in blue and the strobe in alpha. The grid is <paramref name="grid"/>
        /// with <paramref name="spin"/> as its spin timer and <paramref name="strobe"/> (white
        /// when not given) as its strobe output; with <paramref name="fromBuffer"/> the
        /// manager's buffers are live as well. Texel 0 reads <paramref name="firstChannel"/>.
        /// <paramref name="configure"/> sets anything else the probe reads.
        /// </summary>
        static Color[] ReadThrough(string probe, int count, RenderTexture grid, RenderTexture spin,
                                   VRSL_DMXURPLightManager manager, bool fromBuffer,
                                   VRSLGridLayout layout = VRSLGridLayout.Horizontal,
                                   System.Action<Material> configure = null,
                                   Texture strobe = null, int firstChannel = 1)
        {
            var shader = Shader.Find(probe);
            Assert.IsNotNull(shader, $"the {probe} test shader is not in this project");
            var target   = new RenderTexture(count, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(count, 1, TextureFormat.RGBAFloat, false, true);
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var cmd      = new UnityEngine.Rendering.CommandBuffer { name = "VRSL test reads" };
            try
            {
                target.Create();
                material.SetTexture("_VRSLU_DMXGridRenderTexture",         grid);
                material.SetTexture("_VRSLU_DMXGridRenderTextureMovement", grid);
                material.SetTexture("_VRSLU_DMXGridSpinTimer",             spin);
                material.SetTexture("_VRSLU_DMXGridStrobeOutput",          strobe != null ? strobe : Texture2D.whiteTexture);
                material.SetInteger("_ProbeChannelOffset", firstChannel - 1);
                material.SetVector("_VRSLDMXTexelSize",new Vector4(1f / grid.width, 1f / grid.height, grid.width, grid.height));
                material.SetInteger("_VRSLU_DMXGridLayout", (int)layout);
                material.SetBuffer("_VRSLU_DMXChannels",    manager.ChannelBuffer);
                material.SetBuffer("_VRSLU_DMXMovement",    manager.MovementBuffer);
                material.SetBuffer("_VRSLU_DMXSpinPhase",   manager.SpinPhaseBuffer);
                material.SetBuffer("_VRSLU_DMXStrobePhase", manager.StrobePhaseBuffer);
                material.SetInteger("_VRSLU_DMXChannelCount", fromBuffer ? manager.ChannelCount : 0);
                configure?.Invoke(material);
                cmd.SetRenderTarget(target);
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(cmd);

                var previous = RenderTexture.active;
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, count, 1), 0, 0, false);
                readback.Apply(false);
                RenderTexture.active = previous;
                return readback.GetPixels();
            }
            finally
            {
                cmd.Release();
                target.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>
        /// N31. Both includes' read functions return, for every channel the rig patches, the
        /// value, the movement value and the spin phase the manager holds for it: from the
        /// channel buffer while it is live, with a grid of other values bound beside it, and
        /// from a grid carrying the same values when it is not.
        /// </summary>
        [UnityTest]
        public IEnumerator N31_both_includes_read_a_channel_alike_from_the_buffer_and_the_grid(
            [Values("Hidden/VRSL-URP/Tests/UrpReads", "Hidden/VRSL-URP/Tests/BuiltInReads")] string probe)
        {
            var rig = VRSLDMXRig.Build();
            var painted = new List<VRSLDMXRig.PaintedGrid>();
            try
            {
                // Smoothing off, so the movement buffer holds the channel values themselves.
                rig.Manager.movementSmoothingMax = 0f;
                rig.Manager.movementSmoothingMin = 0f;
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 2;
                yield return rig.Step(4);

                int count  = PatchedChannels;
                var flat   = rig.Manager.PublishedChannels.ToArray();
                var phases = new float[rig.Manager.SpinPhaseBuffer.count];
                rig.Manager.SpinPhaseBuffer.GetData(phases);
                var values = new float[count];
                var spin   = new float[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = flat[i] / 255f;
                    spin[i]   = phases[i];
                }
                Assert.IsTrue(spin.Any(p => p > 0f), "no channel has a spin phase, so the spin half proves nothing");

                var size = SizeOf(VRSLGridLayout.Horizontal);
                var grid      = VRSLDMXRig.PaintGrid(values, VRSLGridLayout.Horizontal, size.x, size.y);
                var spinGrid  = VRSLDMXRig.PaintGrid(spin, VRSLGridLayout.Horizontal, size.x, size.y);
                var otherGrid = VRSLDMXRig.PaintGrid(ImageValues(count), VRSLGridLayout.Horizontal, size.x, size.y);
                painted.AddRange(new[] { grid, spinGrid, otherGrid });

                var fromGrid   = ReadThrough(probe, count, grid.Texture, spinGrid.Texture, rig.Manager, fromBuffer: false);
                var fromBuffer = ReadThrough(probe, count, otherGrid.Texture, otherGrid.Texture, rig.Manager, fromBuffer: true);
                var wrong = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    foreach (var (how, read) in new[] { ("grid", fromGrid[i]), ("buffer", fromBuffer[i]) })
                    {
                        if (Mathf.Abs(read.r - values[i]) > Half || Mathf.Abs(read.g - values[i]) > Half
                         || Mathf.Abs(read.b - spin[i]) > 1e-3f)
                            wrong.Add($"ch {i + 1} from the {how}: value {read.r * 255f:F2}, movement {read.g * 255f:F2}, "
                                    + $"spin {read.b:F4}; sent {values[i] * 255f:F0}, spin {spin[i]:F4}");
                    }
                }
                Assert.IsEmpty(wrong, $"{wrong.Count} reads differ from what the manager holds: "
                                    + string.Join("; ", wrong.Take(30)));
            }
            finally
            {
                rig.Dispose();
                foreach (var p in painted) p.Dispose();
            }
        }

        /// <summary>How many channels a layout's decode textures carry: 13 to a row of
        /// 2-pixel cells on Horizontal and Vertical, 50 sectors on Legacy.</summary>
        internal static int Capacity(VRSLGridLayout layout)
        {
            if (layout == VRSLGridLayout.Legacy) return 50 * 13;
            var size = SizeOf(layout);
            return 13 * size.y / size.x * 13;
        }

        /// <summary>
        /// G17. Every channel the layout carries, painted in its own cell
        /// with a value no neighbour shares, reads back as its own value through the compute
        /// and through both includes' value, movement and raw (spin) reads. The cells come from
        /// <see cref="VRSLDMXRig.OwnCellsOf"/>, not from a reader, so the row says nothing about
        /// a reader by construction. Channels sharing a Legacy texel are left out.
        /// </summary>
        [UnityTest]
        public IEnumerator G17_every_reader_takes_a_channel_from_its_own_cell(
            [Values(VRSLGridLayout.Horizontal, VRSLGridLayout.Vertical, VRSLGridLayout.Legacy)] VRSLGridLayout layout)
        {
            var size  = SizeOf(layout);
            // Every channel the layout carries (2-pixel cells, 13 to a row): on Horizontal
            // that is the whole of universe 3, up to its last padding slot at 1560.
            int count = layout == VRSLGridLayout.Legacy ? PatchedChannels : Capacity(layout);
            var rig = VRSLDMXRig.Build();
            VRSLDMXRig.PaintedGrid painted = null;
            RenderTexture white = null;
            VRSLGridTextures grid = null;
            try
            {
                yield return rig.Step(2);
                var values = new float[count];
                for (int i = 0; i < count; i++) values[i] = (i % 251 + 2) / 255f;
                painted = VRSLDMXRig.PaintGrid(values, VRSLDMXRig.OwnCellsOf(count, layout, size.x, size.y),
                                               layout, size.x, size.y);
                white = Solid(Color.white);
                grid  = ScriptableObject.CreateInstance<VRSLGridTextures>();
                var set = new VRSLGridTextures.Set
                {
                    main = painted.Texture, movement = painted.Texture, strobe = white, spinTimer = painted.Texture,
                };
                grid.horizontal = grid.vertical = grid.legacy = set;
                rig.Manager.ChannelSource = null;
                rig.Manager.enabled       = false;
                rig.Manager.gridTextures  = grid;
                rig.Manager.dmxInput      = (VRSLDmxInput)layout;
                rig.Manager.enabled       = true;
                yield return rig.Step(3);
                Assert.AreEqual(0, rig.Manager.ChannelCount, "the manager is still reading the channel buffer");
                // The compute readback decodes in whatever layout the manager reads.
                Assert.AreEqual((VRSLGridLayout?)layout, rig.Manager.GridLayout,
                    $"the manager reads {rig.Manager.GridLayout?.ToString() ?? "no grid"}, so the compute half "
                  + $"would judge a {layout} grid through another layout's cells");

                var wrong = new Dictionary<string, List<int>>();
                void Check(string reader, int channel, float read)
                {
                    if (painted.Shared.Contains(channel) || Mathf.Abs(read - values[channel - 1]) <= Half) return;
                    if (!wrong.TryGetValue(reader, out var list)) wrong[reader] = list = new List<int>();
                    list.Add(channel);
                }

                var compute = rig.ReadGridChannels(count);
                for (int i = 0; i < count; i++) Check("compute", i + 1, compute[i]);
                foreach (var probe in new[] { "Hidden/VRSL-URP/Tests/UrpReads", "Hidden/VRSL-URP/Tests/BuiltInReads" })
                {
                    var reads = ReadThrough(probe, count, painted.Texture, painted.Texture, rig.Manager,
                                            fromBuffer: false, layout);
                    string name = probe.Contains("Urp") ? "URP include" : "Built-in include";
                    for (int i = 0; i < count; i++)
                    {
                        Check(name + " value",    i + 1, reads[i].r);
                        Check(name + " movement", i + 1, reads[i].g);
                        Check(name + " raw",      i + 1, reads[i].b);
                    }
                }

                Debug.Log($"[G17] {layout}: {count} channels, {painted.Shared.Count} sharing a texel; "
                        + (wrong.Count == 0 ? "every reader read every channel from its own cell"
                           : string.Join("; ", wrong.Select(w => $"{w.Key} misread {w.Value.Count}: "
                                                                + string.Join(", ", w.Value.Take(20))))));
                Assert.IsEmpty(wrong, $"{layout}: readers that take a channel from a cell not its own: "
                                    + string.Join("; ", wrong.Select(w => $"{w.Key} on {w.Value.Count} channels, "
                                                                         + $"first {string.Join(", ", w.Value.Take(12))}")));
            }
            finally
            {
                rig.Dispose();
                painted?.Dispose();
                if (white != null) { white.Release(); Object.DestroyImmediate(white); }
                if (grid != null) Object.DestroyImmediate(grid);
            }
        }

        /// <summary>
        /// G17, nine-universe mode. A Horizontal grid holding three universes per colour
        /// component (universes 1 to 3 in red, 4 to 6 in green, 7 to 9 in blue), every
        /// channel of all nine painted with a value of its own, reads back through both
        /// includes' value, movement and raw reads with the fixture's nine-universe flag on:
        /// each channel from its own cell's own component. Universes are 520 flat channels,
        /// so the last eight slots of universes 3, 6 and 9 are where a reader counting
        /// universes of 512 goes wrong.
        /// </summary>
        [UnityTest]
        public IEnumerator G17_nine_universe_mode_reads_each_universe_from_its_own_component()
        {
            const VRSLGridLayout layout = VRSLGridLayout.Horizontal;
            var size  = SizeOf(layout);
            int cells = Capacity(layout);
            int count = cells * 3;
            var rig = VRSLDMXRig.Build();
            RenderTexture grid = null;
            try
            {
                yield return rig.Step(2);
                var own    = VRSLDMXRig.OwnCellsOf(cells, layout, size.x, size.y);
                var values = new float[count];
                for (int i = 0; i < count; i++) values[i] = ((i * 13) % 251 + 2) / 255f;
                var pixels = new Color[size.x * size.y];
                for (int c = 0; c < cells; c++)
                    pixels[own[c].y * size.x + own[c].x] =
                        new Color(values[c], values[c + cells], values[c + 2 * cells], 1f);
                grid = Painted(pixels, size, "Painted nine-universe grid");

                var wrong = new Dictionary<string, List<int>>();
                foreach (var probe in new[] { "Hidden/VRSL-URP/Tests/UrpReads", "Hidden/VRSL-URP/Tests/BuiltInReads" })
                {
                    var reads = ReadThrough(probe, count, grid, grid, rig.Manager, fromBuffer: false, layout,
                                            m => m.SetInteger("_NineUniverseMode", 1));
                    string name = probe.Contains("Urp") ? "URP include" : "Built-in include";
                    for (int i = 0; i < count; i++)
                        foreach (var (what, read) in new[] { ("value", reads[i].r), ("movement", reads[i].g), ("raw", reads[i].b) })
                        {
                            if (Mathf.Abs(read - values[i]) <= Half) continue;
                            string key = $"{name} {what}";
                            if (!wrong.TryGetValue(key, out var list)) wrong[key] = list = new List<int>();
                            list.Add(i + 1);
                        }
                }

                Debug.Log($"[G17] nine-universe: {count} channels; "
                        + (wrong.Count == 0 ? "every read took its channel from its own cell and component"
                           : string.Join("; ", wrong.Select(w => $"{w.Key} misread {w.Value.Count}: "
                                                                + string.Join(", ", w.Value.Take(20))))));
                Assert.IsEmpty(wrong, "nine-universe reads that take a channel from a cell or component not its own: "
                                    + string.Join("; ", wrong.Select(w => $"{w.Key} on {w.Value.Count} channels, "
                                                                         + $"first {string.Join(", ", w.Value.Take(12))}")));
            }
            finally
            {
                rig.Dispose();
                if (grid != null) { grid.Release(); Object.DestroyImmediate(grid); }
            }
        }

        /// <summary>A float grid of the given pixels, point-sampled and clamped.</summary>
        internal static RenderTexture Painted(Color[] pixels, Vector2Int size, string name)
        {
            var source = new Texture2D(size.x, size.y, TextureFormat.RGBAFloat, false, true);
            try
            {
                source.SetPixels(pixels);
                source.Apply(false);
                var rt = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                         { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                rt.Create();
                Graphics.CopyTexture(source, rt);
                return rt;
            }
            finally { Object.DestroyImmediate(source); }
        }

        /// <summary>A grid of one colour in every texel.</summary>
        internal static RenderTexture Filled(Color colour, Vector2Int size, string name)
        {
            var pixels = new Color[size.x * size.y];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = colour;
            return Painted(pixels, size, name);
        }

        /// <summary>The channels G20 asks about on <paramref name="layout"/>: the unpatched
        /// channel 0, and channels past what the layout carries, the first of them, the next
        /// sector's, and some way beyond.</summary>
        internal static int[] Uncarried(VRSLGridLayout layout)
        {
            int cap = Capacity(layout);
            return new[] { 0, cap + 1, cap + 2, cap + 7, cap + 13, cap + 14, cap + 200, 2 * cap + 5 };
        }

        /// <summary>
        /// G20. A channel the grid doesn't carry reads as a channel at 0 in every reader:
        /// value, movement and spin 0, strobe open. That is channel 0 (an unpatched fixture)
        /// and every channel past the layout's last cell. The grid is painted grey in every
        /// texel and its strobe closed everywhere, so a reader landing on any cell, clamped
        /// edge or another universe's, reads something else. Through the compute's four reads,
        /// both includes' and the public include's (the last drawn as a scene renderer, so it
        /// reads what the manager publishes). Channels the grid does carry are checked beside
        /// them: grey and strobe closed.
        /// </summary>
        [UnityTest]
        public IEnumerator G20_a_channel_the_grid_does_not_carry_reads_as_zero_in_every_reader(
            [Values(VRSLGridLayout.Horizontal, VRSLGridLayout.Vertical, VRSLGridLayout.Legacy)] VRSLGridLayout layout)
        {
            const float Grey = 0.6f;
            var size  = SizeOf(layout);
            int cap   = Capacity(layout);
            var none  = Uncarried(layout);
            var some  = new[] { 1, 7, 13, cap - 12, cap };
            int span  = none.Max() + 1;
            var rig   = VRSLDMXRig.Build();
            RenderTexture grey = null, closed = null;
            VRSLGridTextures grid = null;
            VRSLDmxReadProbe probe = null;
            try
            {
                yield return rig.Step(2);
                grey   = Filled(new Color(Grey, Grey, Grey, 1f), size, "Grey test grid");
                closed = Filled(Color.black, size, "Closed strobe test grid");
                grid   = ScriptableObject.CreateInstance<VRSLGridTextures>();
                var set = new VRSLGridTextures.Set
                {
                    main = grey, raw = grey, movement = grey, strobe = closed, spinTimer = grey,
                };
                grid.horizontal = grid.vertical = grid.legacy = set;
                rig.Manager.ChannelSource = null;
                rig.Manager.enabled       = false;
                rig.Manager.gridTextures  = grid;
                rig.Manager.dmxInput      = (VRSLDmxInput)layout;
                rig.Manager.enabled       = true;
                probe = VRSLDmxReadProbe.Create(rig, span);
                yield return rig.Step(3);
                Assert.AreEqual(0, rig.Manager.ChannelCount, "the manager is still reading the channel buffer");
                Assert.AreEqual((VRSLGridLayout?)layout, rig.Manager.GridLayout);

                // reader -> (channel -> (value-like reads, strobe))
                var readers = new Dictionary<string, System.Func<int, (float[] values, float strobe)>>();
                var compute = Enumerable.Range(0, 4).Select(r => rig.ReadComputeReads(0, span, r)).ToArray();
                readers["compute"] = ch => (new[] { compute[0][ch], compute[1][ch], compute[2][ch] }, compute[3][ch]);
                foreach (var name in new[] { "Hidden/VRSL-URP/Tests/UrpReads", "Hidden/VRSL-URP/Tests/BuiltInReads" })
                {
                    var reads = ReadThrough(name, span, grey, grey, rig.Manager, fromBuffer: false, layout,
                                            strobe: closed, firstChannel: 0);
                    readers[name.Contains("Urp") ? "URP include" : "Built-in include"] =
                        ch => (new[] { reads[ch].r, reads[ch].g, reads[ch].b }, reads[ch].a);
                }
                foreach (bool vertex in new[] { false, true })
                {
                    var reads = probe.Read(rig, vertex, colour: false, first: 0);
                    readers[$"public include, {(vertex ? "vertex" : "fragment")}"] =
                        ch => (new[] { reads[ch].r, reads[ch].g }, reads[ch].b);
                }

                var wrong = new List<string>();
                foreach (var reader in readers)
                {
                    foreach (int ch in none)
                    {
                        var (values, strobe) = reader.Value(ch);
                        if (values.Any(v => Mathf.Abs(v) > Half) || Mathf.Abs(strobe - 1f) > Half)
                            wrong.Add($"{reader.Key} reads ch {ch} as [{string.Join(", ", values.Select(v => (v * 255f).ToString("F1")))}]"
                                    + $", strobe {strobe:F2}");
                    }
                    foreach (int ch in some)
                    {
                        var (values, strobe) = reader.Value(ch);
                        if (values.Any(v => Mathf.Abs(v - Grey) > Half) || Mathf.Abs(strobe) > Half)
                            wrong.Add($"{reader.Key} misreads carried ch {ch}: [{string.Join(", ", values.Select(v => (v * 255f).ToString("F1")))}]"
                                    + $", strobe {strobe:F2}");
                    }
                }

                // A fixture patched wholly past the grid is black.
                foreach (bool vertex in new[] { false, true })
                {
                    var colour = probe.Read(rig, vertex, colour: true, first: 0);
                    var c = colour[cap + 1];
                    if (c.r > Half || c.g > Half || c.b > Half)
                        wrong.Add($"public include colour, {(vertex ? "vertex" : "fragment")}, for a fixture at ch {cap + 1}: {c}");
                }

                Debug.Log($"[G20] {layout}: capacity {cap}; "
                        + (wrong.Count == 0 ? "every reader reads the uncarried channels as 0" : string.Join("; ", wrong.Take(20))));
                Assert.IsEmpty(wrong, $"{layout}: {wrong.Count} reads of channels the grid doesn't carry: "
                                    + string.Join("; ", wrong.Take(24)));
            }
            finally
            {
                probe?.Dispose();
                rig.Dispose();
                foreach (var rt in new[] { grey, closed })
                    if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
                if (grid != null) Object.DestroyImmediate(grid);
            }
        }

        /// <summary>
        /// G18. The pan and tilt a mover body reads through the URP include take the fine
        /// channel after each coarse one, worth a 256th of a coarse step, when the fixture
        /// enables fine channels, and leave it out when it doesn't, as the compute does for
        /// the realtime light. Read from a painted grid at a 180-degree sweep each way.
        /// </summary>
        [UnityTest]
        public IEnumerator G18_the_urp_include_reads_fine_pan_and_tilt()
        {
            const int channel = 27;
            const float coarsePan = 100f / 255f, finePan = 200f / 255f;
            const float coarseTilt = 60f / 255f, fineTilt = 30f / 255f;
            var rig = VRSLDMXRig.Build();
            VRSLDMXRig.PaintedGrid painted = null;
            try
            {
                yield return rig.Step(2);
                var values = new float[channel + 3];
                values[channel - 1] = coarsePan;
                values[channel]     = finePan;
                values[channel + 1] = coarseTilt;
                values[channel + 2] = fineTilt;
                var size = SizeOf(VRSLGridLayout.Horizontal);
                painted = VRSLDMXRig.PaintGrid(values, VRSLGridLayout.Horizontal, size.x, size.y);

                Vector2 Read(bool fine)
                {
                    var read = ReadThrough("Hidden/VRSL-URP/Tests/UrpPanTilt", 1, painted.Texture, painted.Texture,
                                           rig.Manager, fromBuffer: false, configure: m =>
                    {
                        m.SetInteger("_ProbeChannel", channel);
                        m.SetInteger("_EnableDMX", 1);
                        m.SetInteger("_EnableFineChannels", fine ? 1 : 0);
                        m.SetFloat("_MaxMinPanAngle", 180f);
                        m.SetFloat("_MaxMinTiltAngle", 180f);
                    })[0];
                    return new Vector2(read.r, read.g);
                }

                // Degrees across the full 360-degree sweep, centred on zero.
                var coarse   = new Vector2(coarsePan * 360f - 180f, coarseTilt * 360f - 180f);
                var withFine = new Vector2((coarsePan + finePan / 256f) * 360f - 180f,
                                           (coarseTilt + fineTilt / 256f) * 360f - 180f);
                var off = Read(fine: false);
                var on  = Read(fine: true);
                Debug.Log($"[G18] fine channels off: pan {off.x:F4}, tilt {off.y:F4}; "
                        + $"on: pan {on.x:F4}, tilt {on.y:F4}");

                Assert.That(off.x, Is.EqualTo(coarse.x).Within(0.01f), "pan with fine channels off");
                Assert.That(off.y, Is.EqualTo(coarse.y).Within(0.01f), "tilt with fine channels off");
                Assert.That(on.x, Is.EqualTo(withFine.x).Within(0.01f),
                    $"pan with fine channels on reads {on.x:F4} where the fine channel puts it at {withFine.x:F4}");
                Assert.That(on.y, Is.EqualTo(withFine.y).Within(0.01f),
                    $"tilt with fine channels on reads {on.y:F4} where the fine channel puts it at {withFine.y:F4}");
            }
            finally
            {
                rig.Dispose();
                painted?.Dispose();
            }
        }

        /// <summary>
        /// G19. Every reader samples the grid texel by texel whatever filter the grid texture
        /// carries: G17's grid read through the compute and both includes' value, movement and
        /// raw reads gives the same value for every channel with the texture set to Bilinear
        /// as set to Point. A reader taking the texture's own sampler blends a channel with
        /// its neighbours on a grid built from render textures left at Unity's default filter.
        /// </summary>
        [UnityTest]
        public IEnumerator G19_every_reader_ignores_the_grid_textures_filter(
            [Values(VRSLGridLayout.Horizontal, VRSLGridLayout.Vertical, VRSLGridLayout.Legacy)] VRSLGridLayout layout)
        {
            var size  = SizeOf(layout);
            int count = layout == VRSLGridLayout.Legacy ? PatchedChannels : Mathf.Min(size.y / 2 * 13, 1536);
            var rig = VRSLDMXRig.Build();
            VRSLDMXRig.PaintedGrid painted = null;
            RenderTexture white = null;
            VRSLGridTextures grid = null;
            try
            {
                yield return rig.Step(2);
                var values = new float[count];
                for (int i = 0; i < count; i++) values[i] = (i % 251 + 2) / 255f;
                painted = VRSLDMXRig.PaintGrid(values, VRSLDMXRig.OwnCellsOf(count, layout, size.x, size.y),
                                               layout, size.x, size.y);
                white = Solid(Color.white);
                grid  = ScriptableObject.CreateInstance<VRSLGridTextures>();
                var set = new VRSLGridTextures.Set
                {
                    main = painted.Texture, movement = painted.Texture, strobe = white, spinTimer = painted.Texture,
                };
                grid.horizontal = grid.vertical = grid.legacy = set;
                rig.Manager.ChannelSource = null;
                rig.Manager.enabled       = false;
                rig.Manager.gridTextures  = grid;
                rig.Manager.dmxInput      = (VRSLDmxInput)layout;
                rig.Manager.enabled       = true;
                yield return rig.Step(3);
                Assert.AreEqual(0, rig.Manager.ChannelCount, "the manager is still reading the channel buffer");

                var reads = new Dictionary<string, float[]>[2];
                foreach (var filter in new[] { FilterMode.Point, FilterMode.Bilinear })
                {
                    painted.Texture.filterMode = filter;
                    var byReader = reads[(int)filter] = new Dictionary<string, float[]>
                    {
                        ["compute"] = rig.ReadGridChannels(count),
                    };
                    foreach (var probe in new[] { "Hidden/VRSL-URP/Tests/UrpReads", "Hidden/VRSL-URP/Tests/BuiltInReads" })
                    {
                        var probed = ReadThrough(probe, count, painted.Texture, painted.Texture, rig.Manager,
                                                 fromBuffer: false, layout);
                        string name = probe.Contains("Urp") ? "URP include" : "Built-in include";
                        byReader[name + " value"]    = probed.Select(c => c.r).ToArray();
                        byReader[name + " movement"] = probed.Select(c => c.g).ToArray();
                        byReader[name + " raw"]      = probed.Select(c => c.b).ToArray();
                    }
                }
                Assert.IsTrue(reads[(int)FilterMode.Point]["URP include value"].Distinct().Count() > 100,
                    "the point-sampled reads hardly vary, so comparing them with the filtered ones proves nothing");

                var moved = new Dictionary<string, List<int>>();
                foreach (var reader in reads[(int)FilterMode.Point].Keys)
                {
                    var point = reads[(int)FilterMode.Point][reader];
                    var blend = reads[(int)FilterMode.Bilinear][reader];
                    var list  = Enumerable.Range(0, count).Where(i => Mathf.Abs(point[i] - blend[i]) > Half)
                                          .Select(i => i + 1).ToList();
                    if (list.Count > 0) moved[reader] = list;
                }

                Debug.Log($"[G19] {layout}: {count} channels; "
                        + (moved.Count == 0 ? "no reader's value changed with the grid's filter"
                           : string.Join("; ", moved.Select(m => $"{m.Key} changed on {m.Value.Count}: "
                                                                + string.Join(", ", m.Value.Take(20))))));
                Assert.IsEmpty(moved, $"{layout}: readers whose value depends on the grid texture's filter: "
                                    + string.Join("; ", moved.Select(m => $"{m.Key} on {m.Value.Count} channels, "
                                                                         + $"first {string.Join(", ", m.Value.Take(12))}")));
            }
            finally
            {
                rig.Dispose();
                painted?.Dispose();
                if (white != null) { white.Release(); Object.DestroyImmediate(white); }
                if (grid != null) Object.DestroyImmediate(grid);
            }
        }

        /// <summary>
        /// Paint a Legacy video grid the way a show's video carries it: 26 cells across, an
        /// even sector's 13 channels in cells 0 to 12 and the odd sector's in 13 to 25, and a
        /// row of two sectors per cell height from the bottom, also 26 to the grid. Cell c
        /// covers [c, c + 1) of 26, so a texel belongs to the cell its centre falls in.
        /// </summary>
        static void PaintLegacyVideo(VRSLGridTextures.Set set, float[] values)
        {
            var main  = (CustomRenderTexture)set.main;
            var video = (RenderTexture)main.material.GetTexture("_DMXTexture");
            var pixels = new Color[video.width * video.height];
            for (int y = 0; y < video.height; y++)
                for (int x = 0; x < video.width; x++)
                {
                    int column = Mathf.FloorToInt((x + 0.5f) * 26f / video.width);
                    int row    = Mathf.FloorToInt((y + 0.5f) * 26f / video.height);
                    int ch     = (2 * row + column / 13) * 13 + column % 13;
                    float v    = ch < values.Length ? values[ch] : 0f;
                    pixels[y * video.width + x] = new Color(v, v, v, 1f);
                }
            Blit(pixels, video);
        }

        static void Blit(Color[] pixels, RenderTexture target)
        {
            var source = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true)
                         { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            try
            {
                source.SetPixels(pixels);
                source.Apply(false);
                Graphics.Blit(source, target);
            }
            finally { Object.DestroyImmediate(source); }
        }

        /// <summary>
        /// G21. A Legacy video grid in the video's own layout (<see cref="PaintLegacyVideo"/>),
        /// decoded by the shipped Legacy chain, every sector's dimmer (slot 5) full and its
        /// strobe channel (slot 6) full on every third sector and at zero on the rest: through
        /// the compute and both includes, every third sector strobes and every other sector's
        /// strobe stays open on every frame. A strobe stage reading a neighbouring cell takes
        /// its status from the dimmer and closes a held fixture; one reading another row
        /// strobes the wrong sectors.
        /// </summary>
        [UnityTest]
        public IEnumerator G21_each_legacy_sector_strobes_from_its_own_strobe_channel()
        {
            const int sectors = 50;
            bool Strobes(int sector) => sector % 3 == 0;
            var asset = VRSLGridSwitchTests.Shipped();
            var modes = VRSLGridSwitchTests.Snapshot(asset);
            var rig = VRSLDMXRig.Build(withSource: false);
            try
            {
                VRSLDMXRig.ClearGridTextures(asset.legacy);
                rig.Manager.SetDmxInput((int)VRSLDmxInput.LegacyGrid);
                yield return VRSLGridSwitchTests.PastTheChainsStartUp(rig);
                Assert.AreEqual((VRSLGridLayout?)VRSLGridLayout.Legacy, rig.Manager.GridLayout);

                var values = new float[sectors * 13];
                for (int s = 0; s < sectors; s++)
                {
                    values[s * 13 + 4] = 1f;
                    values[s * 13 + 5] = Strobes(s) ? 1f : 0f;
                }
                PaintLegacyVideo(asset.legacy, values);
                yield return rig.Step(30);

                var live = rig.Manager.LiveTextures;
                var open = new Dictionary<string, int[]>();   // reader -> frames open, per sector
                const int frames = 60;
                for (int f = 0; f < frames; f++)
                {
                    yield return null;
                    rig.RenderFrame();
                    var reads = new Dictionary<string, float[]>
                    {
                        ["compute"] = rig.ReadComputeReads(1, sectors * 13, 3),
                    };
                    foreach (var probe in new[] { "Hidden/VRSL-URP/Tests/UrpReads", "Hidden/VRSL-URP/Tests/BuiltInReads" })
                        reads[probe.Contains("Urp") ? "URP include" : "Built-in include"] =
                            ReadThrough(probe, sectors * 13, live.main, live.spinTimer, rig.Manager, fromBuffer: false,
                                        VRSLGridLayout.Legacy, strobe: live.strobe).Select(c => c.a).ToArray();
                    foreach (var pair in reads)
                    {
                        if (!open.TryGetValue(pair.Key, out var counts)) open[pair.Key] = counts = new int[sectors];
                        for (int s = 0; s < sectors; s++)
                            if (pair.Value[s * 13 + 5] > 0.5f) counts[s]++;
                    }
                }

                var wrong = new List<string>();
                foreach (var pair in open)
                {
                    var still = Enumerable.Range(0, sectors).Where(s => Strobes(s) && (pair.Value[s] == 0 || pair.Value[s] == frames));
                    var dark  = Enumerable.Range(0, sectors).Where(s => !Strobes(s) && pair.Value[s] < frames);
                    if (still.Any())
                        wrong.Add($"{pair.Key}: strobing sectors that never changed state: {string.Join(", ", still)}");
                    if (dark.Any())
                        wrong.Add($"{pair.Key}: held sectors that closed (frames open of {frames}): "
                                + string.Join(", ", dark.Select(s => $"{s} ({pair.Value[s]})")));
                }
                Debug.Log("[G21] " + (wrong.Count == 0
                    ? $"every reader strobed the {Enumerable.Range(0, sectors).Count(Strobes)} strobing sectors "
                    + "and held the rest open on every frame"
                    : string.Join("; ", wrong)));
                Assert.IsEmpty(wrong, string.Join("; ", wrong));
            }
            finally
            {
                rig.Dispose();
                VRSLDMXRig.ClearGridTextures(asset.legacy);
                VRSLGridSwitchTests.Restore(modes);
            }
        }

        /// <summary>The texel of the 50 by 50 Legacy decode textures a reader takes channel
        /// <paramref name="ch"/> from: the texel below and to the left of the Legacy read's
        /// position, which sits on a texel corner everywhere but the shifted rows (sector 23
        /// on) and columns (sector 40 on), where it sits inside the texel named here.</summary>
        static Vector2Int LegacyTexelOf(int ch)
        {
            int sector = (ch - 1) / 13, slot = (ch - 1) % 13, row = sector / 2;
            return new Vector2Int(2 * slot + 25 * (sector & 1), sector >= 23 ? 2 * row - 1 : 2 * row);
        }

        /// <summary>A texel's place in a 4 by 4 tile, as a grey that survives a half: a
        /// texel one over in either direction reads another.</summary>
        static float TileGrey(int x, int y) => ((x & 3) + 4 * (y & 3) + 1) / 17f;

        /// <summary>
        /// G22. Every Legacy read lands on a texel chosen by the layout, not by how a GPU
        /// rounds a position on a texel edge. A 50 by 50 grid, each texel a grey naming its
        /// place in a 4 by 4 tile, read through the compute's value and strobe reads and both
        /// includes' value, movement, spin and strobe reads: every channel takes the texel
        /// below and to the left of its read position (<see cref="LegacyTexelOf"/>). And the
        /// shipped Legacy chain fed a video grid lit only at even columns of even rows: every
        /// texel of the colour, movement, strobe-timer and spin-timer textures reads the video
        /// texel below and to the left of its centre, so all of them light.
        /// </summary>
        [UnityTest]
        public IEnumerator G22_legacy_reads_take_the_texel_the_layout_names()
        {
            const int count = 50 * 13;
            var asset = VRSLGridSwitchTests.Shipped();
            var modes = VRSLGridSwitchTests.Snapshot(asset);
            var rig = VRSLDMXRig.Build(withSource: false);
            RenderTexture tile = null;
            VRSLGridTextures grid = null;
            try
            {
                yield return rig.Step(2);
                var pixels = new Color[50 * 50];
                for (int y = 0; y < 50; y++)
                    for (int x = 0; x < 50; x++)
                        pixels[y * 50 + x] = new Color(TileGrey(x, y), TileGrey(x, y), TileGrey(x, y), 1f);
                tile = Painted(pixels, new Vector2Int(50, 50), "Legacy tile grid");
                grid = ScriptableObject.CreateInstance<VRSLGridTextures>();
                grid.legacy = new VRSLGridTextures.Set { main = tile, movement = tile, strobe = tile, spinTimer = tile };
                rig.Manager.enabled      = false;
                rig.Manager.gridTextures = grid;
                rig.Manager.dmxInput     = VRSLDmxInput.LegacyGrid;
                rig.Manager.enabled      = true;
                yield return rig.Step(3);
                Assert.AreEqual(0, rig.Manager.ChannelCount, "the manager is still reading the channel buffer");
                Assert.AreEqual((VRSLGridLayout?)VRSLGridLayout.Legacy, rig.Manager.GridLayout);

                var reads = new Dictionary<string, float[]>
                {
                    ["compute value"]  = rig.ReadComputeReads(1, count, 0),
                    ["compute strobe"] = rig.ReadComputeReads(1, count, 3),
                };
                foreach (var probe in new[] { "Hidden/VRSL-URP/Tests/UrpReads", "Hidden/VRSL-URP/Tests/BuiltInReads" })
                {
                    var probed = ReadThrough(probe, count, tile, tile, rig.Manager, fromBuffer: false,
                                             VRSLGridLayout.Legacy, strobe: tile);
                    string name = probe.Contains("Urp") ? "URP include" : "Built-in include";
                    reads[name + " value"]    = probed.Select(c => c.r).ToArray();
                    reads[name + " movement"] = probed.Select(c => c.g).ToArray();
                    reads[name + " spin"]     = probed.Select(c => c.b).ToArray();
                    reads[name + " strobe"]   = probed.Select(c => c.a).ToArray();
                }
                var wrong = new List<string>();
                foreach (var pair in reads)
                {
                    var off = Enumerable.Range(1, count).Where(ch =>
                    {
                        var t = LegacyTexelOf(ch);
                        return Mathf.Abs(pair.Value[ch - 1] - TileGrey(t.x, t.y)) > 0.25f / 17f;
                    }).ToList();
                    if (off.Count > 0)
                        wrong.Add($"{pair.Key} took another texel for {off.Count} channels, first "
                                + string.Join(", ", off.Take(12)));
                }

                // The chain's own reads of the video grid.
                rig.Manager.enabled      = false;
                rig.Manager.gridTextures = asset;
                rig.Manager.enabled      = true;
                VRSLDMXRig.ClearGridTextures(asset.legacy);
                yield return VRSLGridSwitchTests.PastTheChainsStartUp(rig);
                var video = (RenderTexture)((CustomRenderTexture)asset.legacy.main).material.GetTexture("_DMXTexture");
                var lit = new Color[video.width * video.height];
                for (int y = 0; y < video.height; y += 2)
                    for (int x = 0; x < video.width; x += 2)
                        lit[y * video.width + x] = Color.white;
                Blit(lit, video);
                yield return rig.Step(30);
                foreach (var (name, rt) in new[] { ("colour", asset.legacy.main), ("movement", asset.legacy.movement),
                                                   ("strobe timer", asset.legacy.strobeTimer), ("spin timer", asset.legacy.spinTimer) })
                {
                    // An unlit read holds 0 exactly; a lit one climbs towards 1 or, in the
                    // timers, gathers phase, which can wrap close to 0 but not onto it.
                    int dark = Texels(rt).Count(c => c.r <= 0f);
                    if (dark > 0) wrong.Add($"{name} ({rt.width}x{rt.height}): {dark} texels read an unlit video texel");
                }

                Debug.Log("[G22] " + (wrong.Count == 0 ? "every Legacy read took the texel the layout names"
                                                        : string.Join("; ", wrong)));
                Assert.IsEmpty(wrong, string.Join("; ", wrong));
            }
            finally
            {
                rig.Dispose();
                VRSLDMXRig.ClearGridTextures(asset.legacy);
                VRSLGridSwitchTests.Restore(modes);
                if (tile != null) { tile.Release(); Object.DestroyImmediate(tile); }
                if (grid != null) Object.DestroyImmediate(grid);
            }
        }

        /// <summary>Every texel of a texture, read back as floats.</summary>
        static Color[] Texels(RenderTexture rt)
        {
            var shot = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            var was  = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                shot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                shot.Apply(false);
                return shot.GetPixels();
            }
            finally
            {
                RenderTexture.active = was;
                Object.DestroyImmediate(shot);
            }
        }

        /// <summary>
        /// N32, the strobe gate. A 13-channel light bar patched on the same sector as one
        /// of the rig's realtime movers, strobing from the synthetic source's Ramp: frame by
        /// frame, the bar glows exactly when the realtime light is open.
        /// </summary>
        [UnityTest]
        public IEnumerator N32_a_shader_driven_fixture_strobes_with_the_realtime_light_on_its_channels()
        {
            // Sector 10's strobe channel reads 136 on the Ramp: above half, the fastest rate.
            const int sector = 10;
            var rig = VRSLDMXRig.Build();
            GameObject bar = null;
            try
            {
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 2;
                rig.PointBeamsDown();

                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + UrpIncludePrefabs[1]);
                bar = Object.Instantiate(prefab, rig.Floor.transform.parent);
                // Clear of the rig, turned so the side it glows from faces the camera.
                bar.transform.localPosition = new Vector3(-10f, 1.5f, -6f);
                bar.transform.localRotation = Quaternion.Euler(UrpIncludeFacing[1]);
                foreach (var s in bar.GetComponentsInChildren<VRSLDMXStatic>(true))
                {
                    s.useLegacySectorMode = true;
                    s.sector              = sector;
                    s.enableStrobe        = true;
                }
                var view = rig.AddSecondaryCamera(targetSize: 128, eye: new Vector3(-10f, 1.5f, -8f),
                                                  lookAt: new Vector3(-10f, 1.5f, -6f));
                VRSLCameraFilter.RegisterMainView(view.Camera);

                yield return rig.Step(4);
                rig.Calibrate();

                var light = new List<bool>();
                var glow  = new List<float>();
                for (int frame = 0; frame < 60; frame++)
                {
                    yield return null;
                    rig.RenderFrame();
                    light.Add(rig.ReadLights()[sector].colorAndIntensity.w > 0f);
                    var image = VRSLImageCompare.Read(view.Target);
                    glow.Add(image.GetPixels32().Max(p => Mathf.Max(p.r, Mathf.Max(p.g, p.b))));
                    Object.DestroyImmediate(image);
                }

                Assert.IsTrue(light.Contains(true) && light.Contains(false),
                    "the realtime light never changed state, so the row compares nothing");
                float lit = glow.Where((g, i) => light[i]).Min(), dark = glow.Where((g, i) => !light[i]).Max();
                Debug.Log($"[N32] {light.Count(o => o)} of {light.Count} frames open; the bar's brightest pixel "
                        + $"is at least {lit} with the light open and at most {dark} with it closed");
                Assert.Greater(lit, dark + 32f,
                    "the bar does not go dark exactly when the realtime light on its channels closes: "
                  + string.Join(" ", light.Select((o, i) => $"{(o ? "open" : "shut")}:{glow[i]:F0}")));
            }
            finally
            {
                if (bar != null) Object.DestroyImmediate(bar);
                rig.Dispose();
            }
        }

        /// <summary>
        /// N33. Under Stream data, with the synthetic
        /// source switched off mid-run, the first frame rendered after the manager next
        /// updates matches the same scene on Stream data with no source at all: every
        /// fixture, shader-driven and realtime, is dark at once, with a grid of other
        /// values bound that nothing may fall back to. Under a grid input the source is on
        /// standby, and switching it off changes nothing: the frame matches the same scene
        /// that never had a source. Both of N29's and N30's sets, strobe held.
        /// </summary>
        [UnityTest]
        public IEnumerator N33_switching_the_source_off_darkens_stream_data_and_leaves_a_grid_alone()
        {
            var grid = ImageValues(VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 25);
            var sets = new[]
            {
                (UrpIncludePrefabs, UrpIncludeFacing, new Vector3(6f, 2f, -15f)),
                (MovingPrefabs, new Vector3[MovingPrefabs.Length], new Vector3(4.4f, 2f, -12f)),
            };
            foreach (var (prefabs, facing, eye) in sets)
            {
                Texture2D switchedOff = null, dark = null, onStandby = null, never = null;
                try
                {
                    yield return CaptureLayout(prefabs, VRSLGridLayout.Horizontal, grid, t => switchedOff = t,
                                               source: VRSL_SyntheticDMXChannelSource.Pattern.Fixtures,
                                               facing: facing, eye: eye,
                                               lateChange: rig => rig.Source.enabled = false);
                    yield return CaptureLayout(prefabs, VRSLGridLayout.Horizontal, grid, t => dark = t,
                                               facing: facing, eye: eye, input: VRSLDmxInput.StreamData);
                    yield return CaptureLayout(prefabs, VRSLGridLayout.Horizontal, grid, t => onStandby = t,
                                               source: VRSL_SyntheticDMXChannelSource.Pattern.Fixtures,
                                               facing: facing, eye: eye, input: VRSLDmxInput.HorizontalGrid,
                                               lateChange: rig => rig.Source.enabled = false);
                    yield return CaptureLayout(prefabs, VRSLGridLayout.Horizontal, grid, t => never = t,
                                               facing: facing, eye: eye);
                    Assert.Greater(LitShare(never), 0.01f,
                        "the frame lit from the grid is nearly black, so nothing could fall back to it");

                    var result = VRSLImageCompare.Compare(dark, switchedOff);
                    Debug.Log($"[N33] {prefabs[0]} and the rest, Stream data with the source switched off "
                            + $"against Stream data with none: {result}");
                    if (result.DifferingPercent > SameValuesBudget)
                        VRSLImageCompare.WriteImages(EvidenceFolder, "N33-dark", dark, switchedOff);
                    Assert.LessOrEqual(result.DifferingPercent, SameValuesBudget,
                        $"a frame after the source switched off under Stream data is not dark ({result}). "
                      + $"Images in {EvidenceFolder}");

                    result = VRSLImageCompare.Compare(never, onStandby);
                    Debug.Log($"[N33] {prefabs[0]} and the rest, the Horizontal grid with a source switched "
                            + $"off on standby against no source: {result}");
                    if (result.DifferingPercent > SameValuesBudget)
                        VRSLImageCompare.WriteImages(EvidenceFolder, "N33-standby", never, onStandby);
                    Assert.LessOrEqual(result.DifferingPercent, SameValuesBudget,
                        $"a source on standby changed the grid's frame ({result}). Images in {EvidenceFolder}");
                }
                finally
                {
                    if (switchedOff != null) Object.DestroyImmediate(switchedOff);
                    if (dark        != null) Object.DestroyImmediate(dark);
                    if (onStandby   != null) Object.DestroyImmediate(onStandby);
                    if (never       != null) Object.DestroyImmediate(never);
                }
            }
        }

        /// <summary>I1's allowance for edge scatter, which is all a correct frame may move.</summary>
        const float EdgeBudget = 0.25f;

        internal static string EvidenceFolder =>
            System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath)!.FullName,
                                   "VRSL-Benchmarks", "image-failures");
    }
}
#endif
