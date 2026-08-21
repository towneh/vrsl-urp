using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Shows every channel of a universe as a cell shaded by its value, live, from
    /// whichever DMX source is actually driving the scene.
    ///
    /// It answers the questions a lit scene can't. Whether the values are what the desk
    /// sent, how long ago each universe was last heard from,
    /// which of the two paths the fixtures are really reading, whether a universe has
    /// gone quiet, and whether the channel a desk is sending is the channel a fixture
    /// is patched at. A patch off by one lights every fixture and moves every head,
    /// just reading its neighbour's values, which looks like a lighting design rather
    /// than a fault.
    ///
    /// Read-only by design. It never writes a channel, so it can never be the cause of
    /// the fault it is being used to chase.
    ///
    /// Menu: VRSL → URP → DMX Monitor. Play mode only — neither manager has
    /// <c>[ExecuteAlways]</c>, so nothing is initialised while the scene is stopped.
    /// </summary>
    public class VRSL_DMXMonitorWindow : EditorWindow
    {
        // ── Shape ─────────────────────────────────────────────────────────────
        enum View
        {
            /// <summary>13 wide by 40 rows, VRSL's own packing. One row is one
            /// 13-channel fixture, so a patch off by one shears diagonally.</summary>
            Fixture,
            /// <summary>32 wide by 16 rows. Channel numbers land on round
            /// boundaries, so a given channel is findable by eye.</summary>
            Desk,
            /// <summary>Every universe at once, one row each. Which universes are
            /// live, before picking one to look at.</summary>
            Overview,
        }

        enum Ramp { Heat, Grey, Change }

        const int Slots  = VRSLDMX.SlotsPerUniverse;        // 520
        const int Usable = VRSLDMX.UsableSlotsPerUniverse;  // 512

        // Sampling and repainting are throttled together: there is no value in
        // resolving a 44 Hz signal at the editor's frame rate, and this runs
        // alongside whatever else the editor is doing.
        const double SampleInterval = 1.0 / 30.0;
        // How long after the last draw the window keeps asking to be repainted.
        // Longer than a frame so an idle editor sustains the chain, short enough
        // that a window hidden behind a tab parks within a blink.
        const double DrawnWindow = 0.5;

        // ── Persisted between docks ───────────────────────────────────────────
        [SerializeField] View _view = View.Fixture;
        [SerializeField] Ramp _ramp = Ramp.Heat;
        [SerializeField] bool _verify;
        [SerializeField] int  _page;

        // ── Sampled values ────────────────────────────────────────────────────
        byte[] _values;
        byte[] _previous;
        int    _origin = -1;    // flat slot _values[0] corresponds to
        int    _slots;          // slots _values covers

        // ── Texture-path readback ─────────────────────────────────────────────
        GraphicsBuffer _readback;
        bool           _readbackPending;
        int            _generation;
        byte[]         _sampled;      // compute-path values, only while verifying
        string         _verifyReport;

        // ── Presentation ──────────────────────────────────────────────────────
        Texture2D _cells;
        Color32[] _pixels;
        static Color32[] s_heat, s_grey;

        double _lastDrawn, _lastRepaint, _lastSample;
        int    _hover = -1;
        Rect   _gridRect;

        List<Patch> _patches;

        struct Patch
        {
            public string name;
            public int    first;   // 1-based flat channel
            public int    span;
            public bool   fiveCh;
        }

        static readonly string[] Roles13 =
        {
            "pan", "pan fine", "tilt", "tilt fine", "cone / zoom", "dimmer", "strobe",
            "red", "green", "blue", "gobo spin", "gobo select", "smoothing",
        };
        static readonly string[] Roles5 = { "dimmer", "red", "green", "blue", "strobe" };

        [MenuItem("VRSL/URP/DMX Monitor", false, 401)]
        public static void Open()
        {
            var window = GetWindow<VRSL_DMXMonitorWindow>();
            window.titleContent = new GUIContent("DMX Monitor");
            window.minSize = new Vector2(380f, 300f);
            window.Show();
        }

        void OnEnable()
        {
            titleContent = new GUIContent("DMX Monitor");
            wantsMouseMove = true;
            EditorApplication.update            += OnEditorUpdate;
            EditorApplication.hierarchyChanged  += InvalidatePatches;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            EditorApplication.update            -= OnEditorUpdate;
            EditorApplication.hierarchyChanged  -= InvalidatePatches;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            Release();
        }

        void OnPlayModeChanged(PlayModeStateChange _)
        {
            InvalidatePatches();
            Release();
        }

        void InvalidatePatches() => _patches = null;

        void Release()
        {
            // Any readback still in flight resolves against a buffer that is about to
            // go away. Moving the generation on makes those callbacks no-ops.
            _generation++;
            _readbackPending = false;
            _readback?.Release();
            _readback = null;
            if (_cells != null) DestroyImmediate(_cells);
            _cells  = null;
            _pixels = null;
            _values = _previous = _sampled = null;
            _origin = -1;
            _slots  = 0;
            _verifyReport = null;
        }

        /// <summary>
        /// A window docked behind another tab keeps receiving this callback but never
        /// receives OnGUI. Asking for a repaint only while the window is actually being
        /// drawn is what parks the sampling when nobody can see it — and switching back
        /// to the tab draws it once unprompted, which restarts the chain on its own.
        /// </summary>
        void OnEditorUpdate()
        {
            // Nothing to sample while the scene is stopped: the manager has no
            // [ExecuteAlways], so the window draws a fixed help box and asking it
            // to redraw thirty times a second buys nothing. Entering play mode
            // repaints the window, which stamps _lastDrawn and restarts the chain.
            if (!Application.isPlaying) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastDrawn > DrawnWindow) return;
            if (now - _lastRepaint < SampleInterval) return;
            _lastRepaint = now;
            Repaint();
        }

        void OnGUI()
        {
            var mgr = VRSL_URPLightManager.Instance;

            if (Event.current.type == EventType.Repaint)
            {
                _lastDrawn = EditorApplication.timeSinceStartup;
                if (mgr != null && _lastDrawn - _lastSample >= SampleInterval)
                {
                    _lastSample = _lastDrawn;
                    Sample(mgr);
                }
            }

            DrawToolbar(mgr);

            if (mgr == null)
            {
                EditorGUILayout.HelpBox(
                    Application.isPlaying
                        ? "No VRSL URP DMX light manager in the scene. The monitor reads through "
                        + "the manager, so there is nothing to show until one is active."
                        : "Enter play mode. The manager has no [ExecuteAlways], so no channel "
                        + "buffer is uploaded and no CRT chain is running while the scene is stopped.",
                    MessageType.Info);
                return;
            }

            var feed = Classify(mgr);
            DrawSourceHeader(mgr, feed);
            DrawGrid(mgr, feed);
            DrawStatusBar(mgr, feed);
        }

        // ── Which source is live ──────────────────────────────────────────────
        enum Feed
        {
            /// <summary>A source is publishing bytes and the fixtures read them.</summary>
            Buffer,
            /// <summary>No source. The CRT decode chain drives the fixtures.</summary>
            Grid,
            /// <summary>A source is registered but publishing no universes, so the
            /// manager has stopped publishing and the fixtures have fallen back to the
            /// grid. Nothing on screen distinguishes this from having no source at
            /// all, which is the reason it gets its own state.</summary>
            Mute,
        }

        static Feed Classify(VRSL_URPLightManager mgr)
        {
            // Both, not just the count: a source cleared after the manager's last upload
            // leaves the count standing until the next one, and the fixtures are on the
            // grid from that point regardless.
            if (mgr.ChannelSource == null) return Feed.Grid;
            return mgr.ChannelCount > 0 ? Feed.Buffer : Feed.Mute;
        }

        void DrawToolbar(VRSL_URPLightManager mgr)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var view = (View)EditorGUILayout.EnumPopup(
                    _view, EditorStyles.toolbarPopup, GUILayout.Width(90f));
                if (view != _view) { _view = view; ResetSampling(); }

                _ramp = (Ramp)EditorGUILayout.EnumPopup(
                    _ramp, EditorStyles.toolbarPopup, GUILayout.Width(80f));

                GUILayout.Space(8f);

                int universes = mgr != null ? UniverseCount(mgr) : 0;
                using (new EditorGUI.DisabledScope(_view == View.Overview || universes <= 1))
                {
                    if (GUILayout.Button("◀", EditorStyles.toolbarButton, GUILayout.Width(24f)))
                        SetPage(_page - 1, universes);
                    GUILayout.Label(
                        universes > 0 ? $"Universe {_page + 1} / {universes}" : "No universes",
                        EditorStyles.miniLabel, GUILayout.Width(110f));
                    if (GUILayout.Button("▶", EditorStyles.toolbarButton, GUILayout.Width(24f)))
                        SetPage(_page + 1, universes);
                }

                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(mgr == null || Classify(mgr) != Feed.Buffer))
                {
                    bool verify = GUILayout.Toggle(
                        _verify,
                        new GUIContent("Verify",
                            "Also read the channels back through the compute shader and compare "
                            + "against what the source published. Catches a packing or indexing "
                            + "fault against live data rather than against a test pattern. Costs "
                            + "a dispatch and a readback per sample, so it is off by default."),
                        EditorStyles.toolbarButton, GUILayout.Width(52f));
                    if (verify != _verify) { _verify = verify; _verifyReport = null; }
                }
            }
        }

        void SetPage(int page, int universes)
        {
            if (universes <= 0) return;
            page = Mathf.Clamp(page, 0, universes - 1);
            if (page == _page) return;
            _page = page;
            ResetSampling();
        }

        void ResetSampling()
        {
            _origin = -1;
            _hover  = -1;
            _verifyReport = null;
        }

        void DrawSourceHeader(VRSL_URPLightManager mgr, Feed feed)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                switch (feed)
                {
                    case Feed.Buffer:
                        EditorGUILayout.LabelField(
                            "Reading", $"Channel buffer — {mgr.ChannelSource.GetType().Name}");
                        EditorGUILayout.LabelField(
                            "Published", $"{mgr.UniverseCount} universe(s), {mgr.ChannelCount} slots");
                        break;

                    case Feed.Grid:
                        EditorGUILayout.LabelField("Reading", "Video grid — CRT decode chain");
                        EditorGUILayout.LabelField("Grid", GridDescription(mgr));
                        break;

                    case Feed.Mute:
                        EditorGUILayout.HelpBox(
                            $"{mgr.ChannelSource.GetType().Name} is registered but publishing no "
                            + "universes, so the manager has stopped publishing and the fixtures "
                            + "have fallen back to the video grid. A source that has not heard "
                            + "anything yet looks exactly like no source at all from the lights.",
                            MessageType.Warning);
                        EditorGUILayout.LabelField("Grid", GridDescription(mgr));
                        break;
                }

                if (feed != Feed.Buffer && mgr.dmxMainTexture != null)
                    EditorGUILayout.LabelField(" ",
                        "Decoded through the compute shader's IndustryRead path — a legacy-mode "
                        + "or nine-universe grid will not read correctly here.",
                        EditorStyles.wordWrappedMiniLabel);
            }
        }

        static string GridDescription(VRSL_URPLightManager mgr)
        {
            var rt = mgr.dmxMainTexture;
            if (rt == null) return "none assigned — nothing to read";
            return $"{rt.name}  {rt.width}×{rt.height}  ({GridUniverses(rt)} universe(s))";
        }

        // ── Sampling ──────────────────────────────────────────────────────────
        static int UniverseCount(VRSL_URPLightManager mgr)
        {
            if (mgr == null) return 0;
            if (mgr.ChannelCount > 0) return mgr.UniverseCount;
            return GridUniverses(mgr.dmxMainTexture);
        }

        /// <summary>Universes the decode grid holds. The grid is 13 cells wide and each
        /// universe is 40 whole rows, so its capacity follows from its dimensions.</summary>
        static int GridUniverses(RenderTexture rt)
        {
            if (rt == null || rt.width <= 0 || rt.height <= 0) return 0;
            float cell = rt.width / 13f;
            if (cell <= 0f) return 0;
            int rows = Mathf.FloorToInt(rt.height / cell);
            return Mathf.Max(0, rows * 13 / Slots);
        }

        void Sample(VRSL_URPLightManager mgr)
        {
            int universes = UniverseCount(mgr);
            if (universes <= 0) { _slots = 0; return; }

            if (_page >= universes) _page = universes - 1;

            bool overview = _view == View.Overview;
            int origin = overview ? 0 : _page * Slots;
            int slots  = overview ? universes * Slots : Slots;

            if (_values == null || _slots != slots || _origin != origin)
            {
                _values   = new byte[slots];
                _previous = new byte[slots];
                _sampled  = null;
                _slots    = slots;
                _origin   = origin;
            }

            if (Classify(mgr) == Feed.Buffer)
            {
                // Copied here rather than once per sample, so the Change ramp always
                // compares two consecutive sets of values. On the texture path the
                // values land asynchronously and the copy belongs with them.
                System.Array.Copy(_values, _previous, slots);
                var flat = mgr.PublishedChannels;
                for (int i = 0; i < slots; i++)
                {
                    int src = origin + i;
                    _values[i] = (uint)src < (uint)flat.Length ? flat[src] : (byte)0;
                }
                if (_verify) RequestReadback(mgr, origin, slots, compare: true);
                else _verifyReport = null;
            }
            else
            {
                RequestReadback(mgr, origin, slots, compare: false);
            }
        }

        void RequestReadback(VRSL_URPLightManager mgr, int origin, int slots, bool compare)
        {
            if (_readbackPending) return;

            var cs = mgr.computeShader;
            if (cs == null) return;
            if (!compare && mgr.dmxMainTexture == null) return;

            int kernel;
            try { kernel = cs.FindKernel("ValidateChannels"); }
            catch (System.Exception) { return; }

            // OnDisable releases the manager's buffers and nulls them; Instance survives
            // until OnDestroy. A disabled-but-alive manager therefore gets this far.
            if (mgr.ChannelBuffer == null) return;

            if (_readback == null || _readback.count != slots)
            {
                _readback?.Release();
                _readback = new GraphicsBuffer(GraphicsBuffer.Target.Structured, slots, sizeof(float));
            }

            // Buffers and textures bind per kernel, so the accessor's inputs have to be
            // supplied here even though the manager already publishes them as globals.
            cs.SetBuffer(kernel, "_VRSLU_DMXChannels", mgr.ChannelBuffer);
            cs.SetInt("_VRSLU_DMXChannelCount", mgr.ChannelCount);
            if (mgr.dmxMainTexture != null)
            {
                cs.SetTexture(kernel, "_DMXMainTex", mgr.dmxMainTexture);
                cs.SetVector("_VRSLDMXTexelSize", new Vector4(
                    1f / mgr.dmxMainTexture.width, 1f / mgr.dmxMainTexture.height,
                    mgr.dmxMainTexture.width,      mgr.dmxMainTexture.height));
            }
            cs.SetBuffer(kernel, "_VRSLU_ValidationOut", _readback);
            cs.SetInt("_VRSLU_ValidationStart", origin + 1);   // the kernel counts from channel 1
            cs.SetInt("_VRSLU_ValidationCount", slots);
            cs.Dispatch(kernel, Mathf.CeilToInt(slots / 64f), 1, 1);

            int generation = _generation;
            _readbackPending = true;
            AsyncGPUReadback.Request(
                _readback, request => OnReadback(request, generation, origin, compare));
        }

        void OnReadback(AsyncGPUReadbackRequest request, int generation, int origin, bool compare)
        {
            if (generation != _generation) return;     // window closed, or the buffer resized
            _readbackPending = false;
            if (request.hasError || _values == null) return;
            // Paging cannot bump the generation — that would release the buffer this
            // request is resolving against — so the origin is what says whether the answer
            // still belongs to the page on screen.
            if (origin != _origin) return;

            var data = request.GetData<float>();
            int count = Mathf.Min(data.Length, _slots);

            if (!compare)
            {
                System.Array.Copy(_values, _previous, _slots);
                for (int i = 0; i < count; i++)
                    _values[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(data[i] * 255f), 0, 255);
            }
            else
            {
                if (_sampled == null || _sampled.Length != _slots) _sampled = new byte[_slots];
                int mismatches = 0, firstAt = -1;
                for (int i = 0; i < count; i++)
                {
                    byte read = (byte)Mathf.Clamp(Mathf.RoundToInt(data[i] * 255f), 0, 255);
                    _sampled[i] = read;
                    if (read == _values[i] || IsPadding(i)) continue;
                    if (firstAt < 0) firstAt = i;
                    mismatches++;
                }
                _verifyReport = mismatches == 0
                    ? $"Verify: all {count} channels read back as published."
                    : $"Verify: {mismatches} of {count} differ, first at channel "
                      + $"{_origin + firstAt + 1} (published {_values[firstAt]}, read {_sampled[firstAt]}). "
                      + "A constant offset in the channel is an indexing fault; a value that looks "
                      + "like a neighbouring byte is a packing fault.";
            }

            Repaint();
        }

        static bool IsPadding(int index) => index % Slots >= Usable;

        // ── The grid ──────────────────────────────────────────────────────────
        void DrawGrid(VRSL_URPLightManager mgr, Feed feed)
        {
            int universes = UniverseCount(mgr);
            Dimensions(universes, out int cols, out int rows);

            var area = GUILayoutUtility.GetRect(
                0f, 0f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (Event.current.type == EventType.Layout) return;

            if (_slots <= 0 || cols <= 0 || rows <= 0)
            {
                GUI.Label(area, "Nothing published yet.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            const float Gutter = 40f;
            var plot = new Rect(area.x + Gutter, area.y + 2f,
                                Mathf.Max(1f, area.width - Gutter - 4f), Mathf.Max(1f, area.height - 4f));
            _gridRect = Fit(plot, cols, rows);

            if (Event.current.type == EventType.Repaint)
            {
                EnsureCells(cols, rows);
                FillCells(cols, rows);
                // Point sampling drops cells outright when the grid is drawn smaller than
                // it is, which the 520-wide overview always is. Averaging neighbours keeps
                // a lone active channel visible instead of losing it between pixels.
                _cells.filterMode = _gridRect.width < cols ? FilterMode.Bilinear : FilterMode.Point;
                GUI.DrawTexture(_gridRect, _cells);
                DrawSeparators(cols, rows);
                DrawRowLabels(new Rect(area.x, _gridRect.y, Gutter - 4f, _gridRect.height), rows);
            }

            TrackHover(cols, rows);
        }

        void Dimensions(int universes, out int cols, out int rows)
        {
            switch (_view)
            {
                case View.Desk:     cols = 32;    rows = Usable / 32;                 break;
                case View.Overview: cols = Slots; rows = Mathf.Max(1, universes);     break;
                default:            cols = 13;    rows = Slots / 13;                  break;
            }
        }

        /// <summary>Centres a cols×rows grid in <paramref name="area"/>. Snapped to whole
        /// pixels per cell once there is room for it, so a point-sampled grid keeps every
        /// cell the same size instead of some rows landing a pixel wider than others.</summary>
        static Rect Fit(Rect area, int cols, int rows)
        {
            float scale = Mathf.Min(area.width / cols, area.height / rows);
            if (scale >= 2f) scale = Mathf.Floor(scale);
            scale = Mathf.Max(scale, 0.05f);
            float w = cols * scale, h = rows * scale;
            return new Rect(area.x + (area.width - w) * 0.5f,
                            area.y + (area.height - h) * 0.5f, w, h);
        }

        int IndexAt(int row, int col, int cols)
        {
            int index = _view == View.Overview ? row * Slots + col : row * cols + col;
            return index < _slots ? index : -1;
        }

        void EnsureCells(int cols, int rows)
        {
            if (_cells != null && _cells.width == cols && _cells.height == rows) return;
            if (_cells != null) DestroyImmediate(_cells);
            _cells = new Texture2D(cols, rows, TextureFormat.RGBA32, false, false)
            {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp,
                hideFlags  = HideFlags.HideAndDontSave,
            };
            _pixels = new Color32[cols * rows];
        }

        void FillCells(int cols, int rows)
        {
            var heat = Heat;
            var lut  = _ramp == Ramp.Grey ? Grey : heat;
            var padding = new Color32(46, 28, 30, 255);
            var absent  = new Color32(24, 24, 26, 255);

            for (int r = 0; r < rows; r++)
            {
                // Texture2D counts rows from the bottom; channel 1 belongs at the top.
                int dst = (rows - 1 - r) * cols;
                for (int c = 0; c < cols; c++)
                {
                    int i = IndexAt(r, c, cols);
                    Color32 px;
                    if (i < 0)              px = absent;
                    else if (IsPadding(i))  px = padding;
                    else if (_ramp == Ramp.Change)
                    {
                        // Scaled up hard, so a one-step move is visible. This measures
                        // movement, not arrival: a source republishing unchanged values is
                        // dark here, which is what the staleness readout is for.
                        int delta = Mathf.Abs(_values[i] - _previous[i]);
                        px = heat[Mathf.Min(255, delta * 8)];
                    }
                    else px = lut[_values[i]];
                    _pixels[dst + c] = px;
                }
            }
            _cells.SetPixels32(_pixels);
            _cells.Apply(false, false);
        }

        void DrawSeparators(int cols, int rows)
        {
            float cellH = _gridRect.height / rows;
            if (cellH < 6f) return;

            var line = new Color(0f, 0f, 0f, 0.35f);
            for (int r = 1; r < rows; r++)
                EditorGUI.DrawRect(
                    new Rect(_gridRect.x, _gridRect.y + r * cellH, _gridRect.width, 1f), line);

            float cellW = _gridRect.width / cols;
            if (cellW >= 6f)
                for (int c = 1; c < cols; c++)
                    EditorGUI.DrawRect(
                        new Rect(_gridRect.x + c * cellW, _gridRect.y, 1f, _gridRect.height), line);
        }

        static GUIStyle s_rowLabel;

        void DrawRowLabels(Rect gutter, int rows)
        {
            float cellH = _gridRect.height / rows;
            if (cellH < 11f) return;

            var style = s_rowLabel ??=
                new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            int step = cellH < 16f ? 5 : 1;
            for (int r = 0; r < rows; r++)
            {
                if (r % step != 0) continue;
                string label = _view == View.Overview
                    ? $"U{r + 1}"
                    : (r * (_view == View.Desk ? 32 : 13) + 1).ToString();
                GUI.Label(new Rect(gutter.x, gutter.y + r * cellH, gutter.width, cellH), label, style);
            }
        }

        void TrackHover(int cols, int rows)
        {
            var e = Event.current;
            if (e.type != EventType.MouseMove && e.type != EventType.MouseDrag &&
                e.type != EventType.Repaint) return;

            if (!_gridRect.Contains(e.mousePosition)) { _hover = -1; return; }

            int c = Mathf.Clamp((int)((e.mousePosition.x - _gridRect.x) / _gridRect.width  * cols), 0, cols - 1);
            int r = Mathf.Clamp((int)((e.mousePosition.y - _gridRect.y) / _gridRect.height * rows), 0, rows - 1);
            _hover = IndexAt(r, c, cols);
        }

        // ── Status ────────────────────────────────────────────────────────────
        void DrawStatusBar(VRSL_URPLightManager mgr, Feed feed)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    _hover >= 0 ? DescribeCell(mgr) : Summary(mgr),
                    EditorStyles.wordWrappedMiniLabel);

                if (!string.IsNullOrEmpty(_verifyReport))
                    EditorGUILayout.LabelField(_verifyReport, EditorStyles.wordWrappedMiniLabel);
            }
        }

        string DescribeCell(VRSL_URPLightManager mgr)
        {
            int index = _hover;
            if (_values == null || index < 0 || index >= _values.Length) return string.Empty;

            int flat     = _origin + index;              // 0-based flat slot
            int universe = flat / Slots;
            int slot     = flat % Slots;

            if (slot >= Usable)
                return $"Flat {flat + 1} — padding between universe {universe + 1} and {universe + 2}. "
                     + "No desk can address it and nothing reads it; it must stay at 0.";

            byte value = _values[index];
            string text = $"Universe {universe + 1} ch {slot + 1}  ·  flat {flat + 1}  ·  "
                        + $"{value} ({value / 255f:F3})";

            string patch = DescribePatch(flat + 1);
            if (patch != null) text += "  ·  " + patch;
            return text;
        }

        string DescribePatch(int flatChannel)
        {
            EnsurePatches();
            string first = null;
            int extra = 0;
            foreach (var p in _patches)
            {
                int offset = flatChannel - p.first;
                if (offset < 0 || offset >= p.span) continue;
                if (first != null) { extra++; continue; }

                var roles = p.fiveCh ? Roles5 : Roles13;
                first = p.span == 1 || offset >= roles.Length
                    ? p.name
                    : $"{p.name} · {roles[offset]}";
            }
            if (first == null) return null;
            return extra > 0 ? $"{first} (+{extra} more here)" : first;
        }

        string Summary(VRSL_URPLightManager mgr)
        {
            if (_values == null || _slots == 0) return "Waiting for values.";

            int live = 0, peak = 0;
            for (int i = 0; i < _slots; i++)
            {
                if (IsPadding(i)) continue;
                if (_values[i] != 0) live++;
                if (_values[i] > peak) peak = _values[i];
            }

            string text = $"{live} channel(s) above zero, peak {peak}.";

            // Staleness is per universe because each one carries its own age. A universe
            // that has stopped arriving keeps its last values, so the grid alone cannot
            // tell it apart from one holding a static look.
            if (Classify(mgr) == Feed.Buffer && _view != View.Overview)
            {
                text += mgr.TryGetUniverseLatchTime(_page, out double latched)
                    ? $"  Universe {_page + 1} last heard {Time.timeAsDouble - latched:F2}s ago."
                    : $"  Universe {_page + 1} has not been heard from.";
            }

            return text;
        }

        void EnsurePatches()
        {
            if (_patches != null) return;
            _patches = new List<Patch>();

            foreach (var f in Object.FindObjectsByType<VRStageLighting_DMX_RealtimeLight>(
                         FindObjectsSortMode.None))
            {
                _patches.Add(new Patch
                {
                    name   = f.name,
                    first  = f.ComputeAbsoluteChannel(),
                    span   = f.use5ChannelMode ? 5 : 13,
                    fiveCh = f.use5ChannelMode,
                });
            }

            // The static fixture keeps its channel arithmetic private, so it is repeated
            // here from its public fields rather than reached for. Same two expressions.
            foreach (var f in Object.FindObjectsByType<VRStageLighting_DMX_Static>(
                         FindObjectsSortMode.None))
            {
                if (!f.enableDMXChannels) continue;
                int first = f.useLegacySectorMode
                    ? Mathf.Abs(f.sector * 13 + 1)
                    : Mathf.Abs(f.dmxChannel + (f.dmxUniverse - 1) * 512 + (f.dmxUniverse - 1) * 8);
                if (f.singleChannelMode && f.useLegacySectorMode) first += Mathf.Abs(f.Channel);
                _patches.Add(new Patch
                {
                    name  = f.name,
                    first = first,
                    span  = f.singleChannelMode ? 1 : 13,
                });
            }
        }

        // ── Ramps ─────────────────────────────────────────────────────────────
        static Color32[] Grey
        {
            get
            {
                if (s_grey != null) return s_grey;
                s_grey = new Color32[256];
                for (int i = 0; i < 256; i++) s_grey[i] = new Color32((byte)i, (byte)i, (byte)i, 255);
                return s_grey;
            }
        }

        static Color32[] Heat
        {
            get
            {
                if (s_heat != null) return s_heat;
                // Monotonic in lightness so the ramp reads as an ordering rather than as
                // a set of categories, and dark enough at the bottom that zero is
                // obviously zero.
                var stops = new[]
                {
                    new Color32( 12,  14,  24, 255),
                    new Color32( 30,  70, 160, 255),
                    new Color32( 20, 160, 170, 255),
                    new Color32(230, 180,  40, 255),
                    new Color32(255, 250, 220, 255),
                };
                s_heat = new Color32[256];
                for (int i = 0; i < 256; i++)
                {
                    float t = i / 255f * (stops.Length - 1);
                    int a = Mathf.Clamp(Mathf.FloorToInt(t), 0, stops.Length - 2);
                    s_heat[i] = Color32.Lerp(stops[a], stops[a + 1], t - a);
                }
                return s_heat;
            }
        }
    }
}
