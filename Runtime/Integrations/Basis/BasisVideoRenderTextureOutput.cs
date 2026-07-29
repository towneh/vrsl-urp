using UnityEngine;
using Cilbox;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VRSL.URP.BasisIntegration
{
    // RenderTexture sink for BasisMediaPlayer that reproduces the framing the VRSL DMX
    // capture camera did, without a camera. Each frame it draws the player's OutputTexture
    // into Target sampled at four source UVs (one per Target corner), so crop, camera roll,
    // flip and shear are all expressible. Set the four UVs by dragging the corners over the
    // live grid in the inspector, or bake them from the old DMX camera as a starting point.
    [Cilboxable]
    [DisallowMultipleComponent]
    public class BasisVideoRenderTextureOutput : MonoBehaviour
    {
        [Tooltip("Player to read from. If unassigned, GetComponentInParent<BasisMediaPlayer>() is used.")]
        public BasisMediaPlayer Player;

        [Tooltip("RenderTexture that receives the decoded, framed grid. The RAW DMX-grid RT the decode chain reads (the DMX camera's old Target Texture).")]
        public RenderTexture Target;

        // Source UVs sampled at Target's corners. Identity = full frame. Drag in the inspector.
        public Vector2 uvBL = new Vector2(0f, 0f);
        public Vector2 uvBR = new Vector2(1f, 0f);
        public Vector2 uvTR = new Vector2(1f, 1f);
        public Vector2 uvTL = new Vector2(0f, 1f);

        [Tooltip("Edit-mode setup aid: a still image of the DMX grid to drag the corners over when not in Play mode. In Play mode the live decoded frame is shown instead.")]
        public Texture SetupPreview;

        [Tooltip("Clear Target to black when no frame is available (before first frame / after Stop) so the decode chain doesn't latch the last grid.")]
        public bool ClearWhenNoFrame = true;

        [Tooltip("DMX camera to bake the framing from (editor only). Can be deleted after baking.")]
        public Camera SourceCamera;

        [Tooltip("Screen quad the DMX camera filmed (editor only). Can be deleted after baking.")]
        public Renderer SourceScreen;

        [SerializeField, HideInInspector] private Shader blitShader;

        private Material blitMat;
        private bool cleared;

        private void Reset()
        {
            if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
        }

        private void Start()
        {
            if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
            if (Player == null)
            {
                Debug.LogWarning("BasisVideoRenderTextureOutput: no BasisMediaPlayer assigned or found in parents.");
            }
        }

        private void OnDestroy()
        {
            if (blitMat != null) Destroy(blitMat);
        }

        private void EnsureMaterial()
        {
            if (blitMat != null) return;
            var sh = blitShader != null ? blitShader : Shader.Find("Hidden/VRSL-URP/BasisVideoUVBlit");
            if (sh == null) sh = Shader.Find("Unlit/Texture");
            blitMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        }

        private void Update()
        {
            if (Target == null) return;

            Texture src = Player != null ? Player.OutputTexture : null;
            if (src == null)
            {
                if (ClearWhenNoFrame && !cleared)
                {
                    Graphics.Blit(Texture2D.blackTexture, Target);
                    cleared = true;
                }
                return;
            }

            EnsureMaterial();

            // Per-client frame-origin correction. Some Windows GPUs/drivers can't normalize the
            // decoded frame's orientation natively (the D3D11 video-processor mirror is optional),
            // so the player delivers it top-left origin — vertically mirrored vs GPUs that can.
            // The corner UVs are authored GPU-neutral, so mirror each sampled V per client to land
            // on the same content. Matches how BasisVideoMaterialOutput / BasisVideoDisplay fold
            // Player.OutputFrameIsTopLeftOrigin into their own flip; without it the grid comes out
            // flipped wrong on some PCs.
            Vector2 bl = uvBL, br = uvBR, tr = uvTR, tl = uvTL;
            if (Player != null && Player.OutputFrameIsTopLeftOrigin)
            {
                bl.y = 1f - bl.y;
                br.y = 1f - br.y;
                tr.y = 1f - tr.y;
                tl.y = 1f - tl.y;
            }
            BlitUVs(blitMat, src, Target, bl, br, tr, tl);
            cleared = false;
        }

        // Draws src into dst as a fullscreen quad, sampling src at the four given UVs (one per
        // dst corner). Shared by the runtime path and the editor output preview.
        public static void BlitUVs(Material mat, Texture src, RenderTexture dst, Vector2 bl, Vector2 br, Vector2 tr, Vector2 tl)
        {
            if (mat == null || src == null || dst == null) return;
            mat.SetVector("_UvBL", new Vector4(bl.x, bl.y, 0f, 0f));
            mat.SetVector("_UvBR", new Vector4(br.x, br.y, 0f, 0f));
            mat.SetVector("_UvTR", new Vector4(tr.x, tr.y, 0f, 0f));
            mat.SetVector("_UvTL", new Vector4(tl.x, tl.y, 0f, 0f));
            Graphics.Blit(src, dst, mat);
        }

        // Projects the camera's viewport corners onto the screen quad's plane and converts
        // the hits to the quad's UV space — a starting point for the four source UVs that the
        // user then fine-tunes by dragging. Returns false if inputs are missing.
        public static bool ComputeUVsFromCamera(Camera cam, Renderer screen, RenderTexture target,
            out Vector2 bl, out Vector2 br, out Vector2 tr, out Vector2 tl)
        {
            bl = Vector2.zero; br = Vector2.right; tr = Vector2.one; tl = Vector2.up;
            if (cam == null || screen == null) return false;

            Transform qt = screen.transform;
            float savedAspect = cam.aspect;
            bool forced = false;
            if (target != null && target.height > 0)
            {
                cam.aspect = target.width / (float)target.height;
                forced = true;
            }

            Vector2[] vp = { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            Vector2[] outUV = new Vector2[4];
            bool ok = true;

            for (int i = 0; i < 4; i++)
            {
                Ray r = cam.ViewportPointToRay(new Vector3(vp[i].x, vp[i].y, 0f));
                Plane plane = new Plane(qt.forward, qt.position);
                if (!plane.Raycast(r, out float enter))
                {
                    plane = new Plane(-qt.forward, qt.position);
                    if (!plane.Raycast(r, out enter)) { ok = false; break; }
                }
                Vector3 local = qt.InverseTransformPoint(r.GetPoint(enter));
                outUV[i] = new Vector2(local.x + 0.5f, local.y + 0.5f);
            }

            if (forced) cam.aspect = savedAspect;
            if (!ok) return false;

            bl = outUV[0]; br = outUV[1]; tr = outUV[2]; tl = outUV[3];
            return true;
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(BasisVideoRenderTextureOutput))]
    public class BasisVideoRenderTextureOutput_Editor : Editor
    {
        static GUIContent L(string label, string tooltip) => new GUIContent(label, tooltip);

        static readonly Color[] cornerColors = { Color.green, new Color(1f, 0.6f, 0.2f), Color.white, new Color(1f, 0.4f, 1f) };
        static readonly string[] cornerNames = { "BL", "BR", "TR", "TL" };

        private int dragIndex = -1;
        private Material previewMat;
        private RenderTexture previewRT;

        private void OnEnable()
        {
            EditorApplication.update += UpdatePreview;
        }

        private void OnDisable()
        {
            EditorApplication.update -= UpdatePreview;
            if (previewRT != null) { previewRT.Release(); DestroyImmediate(previewRT); previewRT = null; }
            if (previewMat != null) { DestroyImmediate(previewMat); previewMat = null; }
        }

        // Render the output preview OUTSIDE OnInspectorGUI. Doing Graphics.Blit / GL during the
        // inspector repaint corrupts IMGUI (the broken inspector); rendering here and only
        // GUI.DrawTexture-ing in OnInspectorGUI keeps it stable.
        private void UpdatePreview()
        {
            var cfg = target as BasisVideoRenderTextureOutput;
            if (cfg == null) return;
            bool live = Application.isPlaying && cfg.Player != null && cfg.Player.OutputTexture != null;
            Texture tex = live ? cfg.Player.OutputTexture : (cfg.SetupPreview != null ? cfg.SetupPreview : cfg.Target);
            if (tex == null) return;

            float outAspect = (cfg.Target != null && cfg.Target.height > 0) ? (float)cfg.Target.width / cfg.Target.height
                            : (tex.height > 0 ? (float)tex.width / tex.height : 16f / 9f);
            EnsurePreviewRT(outAspect);
            if (previewMat != null && previewRT != null)
            {
                // Match the runtime per-client origin flip so this output preview stays WYSIWYG in
                // play mode on top-left-origin GPUs (see BasisVideoRenderTextureOutput.Update).
                Vector2 bl = cfg.uvBL, br = cfg.uvBR, tr = cfg.uvTR, tl = cfg.uvTL;
                if (live && cfg.Player.OutputFrameIsTopLeftOrigin)
                {
                    bl.y = 1f - bl.y; br.y = 1f - br.y; tr.y = 1f - tr.y; tl.y = 1f - tl.y;
                }
                BasisVideoRenderTextureOutput.BlitUVs(previewMat, tex, previewRT, bl, br, tr, tl);
                Repaint();
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var cfg = (BasisVideoRenderTextureOutput)target;

            var blitProp = serializedObject.FindProperty("blitShader");
            if (blitProp.objectReferenceValue == null)
                blitProp.objectReferenceValue = Shader.Find("Hidden/VRSL-URP/BasisVideoUVBlit");

            EditorGUILayout.PropertyField(serializedObject.FindProperty("Player"), L("DMX Media Player", "The BasisMediaPlayer decoding the DMX-over-video stream."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Target"), L("DMX Grid RT", "The RAW DMX-grid RenderTexture the decode chain reads (the DMX camera's old Target Texture)."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("SetupPreview"), L("Setup Preview", "Edit-mode still of the grid to drag the corners over. Play mode shows the live frame."));

            var pBL = serializedObject.FindProperty("uvBL");
            var pBR = serializedObject.FindProperty("uvBR");
            var pTR = serializedObject.FindProperty("uvTR");
            var pTL = serializedObject.FindProperty("uvTL");
            var props = new[] { pBL, pBR, pTR, pTL };

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Drag the corners to frame the grid", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Put BL/BR/TR/TL on the grid's corners. Rotation/flip = where you place them.", EditorStyles.miniLabel);
            DrawInteractivePreview(cfg, props);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to full frame"))
                {
                    pBL.vector2Value = new Vector2(0, 0);
                    pBR.vector2Value = new Vector2(1, 0);
                    pTR.vector2Value = new Vector2(1, 1);
                    pTL.vector2Value = new Vector2(0, 1);
                }
                if (GUILayout.Button("Rotate 90°")) Rotate90(props);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Flip Horizontal")) FlipH(props);
                if (GUILayout.Button("Flip Vertical")) FlipV(props);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bake starting point from camera (optional)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("SourceCamera"), L("DMX Camera", "Deletable after baking."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("SourceScreen"), L("Screen Quad", "Deletable after baking."));
            using (new EditorGUI.DisabledScope(
                serializedObject.FindProperty("SourceCamera").objectReferenceValue == null ||
                serializedObject.FindProperty("SourceScreen").objectReferenceValue == null))
            {
                if (GUILayout.Button("Bake Framing From Camera"))
                {
                    var cam = serializedObject.FindProperty("SourceCamera").objectReferenceValue as Camera;
                    var screen = serializedObject.FindProperty("SourceScreen").objectReferenceValue as Renderer;
                    var tgt = serializedObject.FindProperty("Target").objectReferenceValue as RenderTexture;
                    if (BasisVideoRenderTextureOutput.ComputeUVsFromCamera(cam, screen, tgt, out var bl, out var br, out var tr, out var tl))
                    {
                        pBL.vector2Value = bl; pBR.vector2Value = br; pTR.vector2Value = tr; pTL.vector2Value = tl;
                    }
                    else Debug.LogWarning("Bake failed: check the Camera and Screen Quad references.");
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(pBL, L("Bottom-Left", ""));
            EditorGUILayout.PropertyField(pBR, L("Bottom-Right", ""));
            EditorGUILayout.PropertyField(pTR, L("Top-Right", ""));
            EditorGUILayout.PropertyField(pTL, L("Top-Left", ""));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ClearWhenNoFrame"));

            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Play-mode drags revert on stop. To save, drag in Edit mode against a Setup Preview, or copy these values.", MessageType.Info);

            serializedObject.ApplyModifiedProperties();
        }

        void DrawInteractivePreview(BasisVideoRenderTextureOutput cfg, SerializedProperty[] uvProps)
        {
            Texture tex = (Application.isPlaying && cfg.Player != null) ? cfg.Player.OutputTexture : null;
            if (tex == null) tex = cfg.SetupPreview != null ? cfg.SetupPreview : cfg.Target;

            // OUTPUT preview: the actual result written to the RT (rendered in UpdatePreview).
            DrawOutputPreview(cfg);
            EditorGUILayout.LabelField("Source (drag corners to crop):", EditorStyles.miniLabel);

            // Render the source preview at its true aspect so an extreme strip (e.g. 1920x208)
            // isn't squished, which would make corner alignment by eye wrong.
            float aspect = 16f / 9f;
            if (tex != null && tex.height > 0) aspect = (float)tex.width / tex.height;
            else if (cfg.Target != null && cfg.Target.height > 0) aspect = (float)cfg.Target.width / cfg.Target.height;

            Rect area = GUILayoutUtility.GetRect(0, 240f, GUILayout.ExpandWidth(true));
            float w = area.width, h = w / aspect;
            if (h > area.height) { h = area.height; w = h * aspect; }
            Rect box = new Rect(area.x + (area.width - w) * 0.5f, area.y + (area.height - h) * 0.5f, w, h);

            if (Event.current.type == EventType.Repaint)
            {
                if (tex != null) GUI.DrawTexture(box, tex, ScaleMode.StretchToFill, false);
                else EditorGUI.DrawRect(box, new Color(0.12f, 0.12f, 0.12f));
                DrawBorder(box, new Color(0.3f, 0.3f, 0.3f));
            }

            Vector2 ToScreen(Vector2 uv) => new Vector2(box.x + uv.x * box.width, box.y + (1f - uv.y) * box.height);
            Vector2 ToUV(Vector2 s) => new Vector2(Mathf.Clamp01((s.x - box.x) / box.width), Mathf.Clamp01(1f - (s.y - box.y) / box.height));

            Vector2[] sp = new Vector2[4];
            for (int i = 0; i < 4; i++) sp[i] = ToScreen(uvProps[i].vector2Value);

            if (Event.current.type == EventType.Repaint)
            {
                Color line = new Color(0.35f, 0.7f, 1f, 0.9f);
                Line(sp[0], sp[1], line); Line(sp[1], sp[2], line); Line(sp[2], sp[3], line); Line(sp[3], sp[0], line);
            }

            Event e = Event.current;
            for (int i = 0; i < 4; i++)
            {
                Rect hr = new Rect(sp[i].x - 6, sp[i].y - 6, 12, 12);
                EditorGUIUtility.AddCursorRect(hr, MouseCursor.MoveArrow);
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(hr, cornerColors[i]);
                    GUI.Label(new Rect(sp[i].x + 8, sp[i].y - 8, 30, 16), cornerNames[i], EditorStyles.whiteMiniLabel);
                }
                if (e.type == EventType.MouseDown && hr.Contains(e.mousePosition)) { dragIndex = i; e.Use(); }
            }

            if (dragIndex >= 0)
            {
                if (e.type == EventType.MouseDrag) { uvProps[dragIndex].vector2Value = ToUV(e.mousePosition); e.Use(); Repaint(); }
                else if (e.type == EventType.MouseUp) { dragIndex = -1; e.Use(); }
            }
        }

        void DrawOutputPreview(BasisVideoRenderTextureOutput cfg)
        {
            float outAspect = (cfg.Target != null && cfg.Target.height > 0) ? (float)cfg.Target.width / cfg.Target.height
                            : (previewRT != null && previewRT.height > 0 ? (float)previewRT.width / previewRT.height : 16f / 9f);

            EditorGUILayout.LabelField("Output → DMX Grid RT:", EditorStyles.miniLabel);
            Rect oa = GUILayoutUtility.GetRect(0, 140f, GUILayout.ExpandWidth(true));
            float w = oa.width, h = w / outAspect;
            if (h > oa.height) { h = oa.height; w = h * outAspect; }
            Rect ob = new Rect(oa.x + (oa.width - w) * 0.5f, oa.y + (oa.height - h) * 0.5f, w, h);
            if (Event.current.type == EventType.Repaint)
            {
                if (previewRT != null) GUI.DrawTexture(ob, previewRT, ScaleMode.StretchToFill, false);
                else EditorGUI.DrawRect(ob, new Color(0.12f, 0.12f, 0.12f));
                DrawBorder(ob, new Color(0.35f, 0.7f, 1f));
            }
        }

        static void Line(Vector2 a, Vector2 b, Color c)
        {
            Matrix4x4 saved = GUI.matrix;
            float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            float len = Vector2.Distance(a, b);
            GUIUtility.RotateAroundPivot(angle, a);
            EditorGUI.DrawRect(new Rect(a.x, a.y - 1f, len, 2f), c);
            GUI.matrix = saved;
        }

        void EnsurePreviewRT(float aspect)
        {
            if (previewMat == null)
            {
                var sh = Shader.Find("Hidden/VRSL-URP/BasisVideoUVBlit");
                if (sh == null) sh = Shader.Find("Unlit/Texture");
                previewMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            int W, H;
            if (aspect >= 1f) { W = 512; H = Mathf.Max(8, Mathf.RoundToInt(512f / aspect)); }
            else { H = 512; W = Mathf.Max(8, Mathf.RoundToInt(512f * aspect)); }
            if (previewRT == null || previewRT.width != W || previewRT.height != H)
            {
                if (previewRT != null) { previewRT.Release(); DestroyImmediate(previewRT); }
                previewRT = new RenderTexture(W, H, 0) { hideFlags = HideFlags.HideAndDontSave };
                previewRT.Create();
            }
        }

        static void Rotate90(SerializedProperty[] p)
        {
            var bl = p[0].vector2Value; var br = p[1].vector2Value; var tr = p[2].vector2Value; var tl = p[3].vector2Value;
            p[0].vector2Value = br; p[1].vector2Value = tr; p[2].vector2Value = tl; p[3].vector2Value = bl;
        }

        // Mirror horizontally: swap the source UVs feeding the left vs right output corners.
        static void FlipH(SerializedProperty[] p)
        {
            var bl = p[0].vector2Value; var br = p[1].vector2Value; var tr = p[2].vector2Value; var tl = p[3].vector2Value;
            p[0].vector2Value = br; p[1].vector2Value = bl; p[2].vector2Value = tl; p[3].vector2Value = tr;
        }

        // Mirror vertically: swap the source UVs feeding the bottom vs top output corners.
        static void FlipV(SerializedProperty[] p)
        {
            var bl = p[0].vector2Value; var br = p[1].vector2Value; var tr = p[2].vector2Value; var tl = p[3].vector2Value;
            p[0].vector2Value = tl; p[1].vector2Value = tr; p[2].vector2Value = br; p[3].vector2Value = bl;
        }

        static void DrawBorder(Rect r, Color c)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), c);
        }
    }
#endif
}
