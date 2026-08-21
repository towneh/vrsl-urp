using UnityEditor;
using UnityEngine;
using VRSL.URP;

namespace VRSL.URP.BasisIntegration
{
    [CustomEditor(typeof(BasisVideoRenderTextureOutput))]
    public class BasisVideoRenderTextureOutput_Editor : Editor
    {
        static GUIContent L(string label, string tooltip) => new GUIContent(label, tooltip);

        static readonly Color[] cornerColors = { Color.green, new Color(1f, 0.6f, 0.2f), Color.white, new Color(1f, 0.4f, 1f) };
        static readonly string[] cornerNames = { "BL", "BR", "TR", "TL" };

        private int dragIndex = -1;
        private Material previewMat;
        private RenderTexture previewRT;
        private double  nextPreview;
        private int     previewKey;
        private Texture previewTex;

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

            // Every editor tick would otherwise blit and ask for another repaint,
            // holding the editor at full rate for as long as the component is
            // selected. Nothing moves in edit mode unless the framing does; in
            // play mode the frame does, so that case is capped rather than
            // skipped.
            int key = (cfg.uvBL, cfg.uvBR, cfg.uvTR, cfg.uvTL).GetHashCode();
            bool framingMoved = key != previewKey || !ReferenceEquals(tex, previewTex);
            if (!live && !framingMoved) return;
            if (live && !framingMoved && EditorApplication.timeSinceStartup < nextPreview) return;
            previewKey  = key;
            previewTex  = tex;
            nextPreview = EditorApplication.timeSinceStartup + 1.0 / 15.0;

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

            VRSL_EditorHeader.Draw();

            var cfg = (BasisVideoRenderTextureOutput)target;

            // Components predating Reset filling this in keep an empty field, and
            // an empty field is not merely a missing cache: the serialized
            // reference is the only thing that pulls the shader into a player
            // build. Offered as a button rather than written on sight, so looking
            // at the component never dirties the scene.
            var blitProp = serializedObject.FindProperty("blitShader");
            if (blitProp != null && blitProp.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox(
                    "No blit shader assigned. It works in the editor, where the shader can be "
                  + "found by name, but a player build leaves it out and the grid is not framed "
                  + "at all.", MessageType.Warning);
                if (GUILayout.Button("Assign the blit shader"))
                    blitProp.objectReferenceValue =
                        Shader.Find(BasisVideoRenderTextureOutput.BlitShaderName);
                EditorGUILayout.Space();
            }

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
            // Derived exactly as UpdatePreview derives it: the two previews showing
            // different frames is worse than either being wrong on its own.
            bool live = Application.isPlaying && cfg.Player != null && cfg.Player.OutputTexture != null;
            Texture tex = live ? cfg.Player.OutputTexture : null;
            if (tex == null) tex = cfg.SetupPreview != null ? cfg.SetupPreview : cfg.Target;
            bool flip = live && cfg.Player.OutputFrameIsTopLeftOrigin;

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
                // The corner handles below live in UV space with v=0 at the bottom of
                // the box, and the runtime flips v per client to land on the same
                // content. A frame whose row 0 is the top of the picture therefore
                // has to be drawn flipped, or a corner dragged onto something
                // visible authors the mirror of it.
                if (tex != null && flip)
                    GUI.DrawTextureWithTexCoords(box, tex, new Rect(0f, 1f, 1f, -1f), false);
                else if (tex != null) GUI.DrawTexture(box, tex, ScaleMode.StretchToFill, false);
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
                // Left button only: a right-click on a handle would otherwise
                // capture the corner and drag it under the context menu.
                if (e.type == EventType.MouseDown && e.button == 0 && hr.Contains(e.mousePosition))
                { dragIndex = i; e.Use(); }
            }

            if (dragIndex >= 0)
            {
                if (e.type == EventType.MouseDrag) { uvProps[dragIndex].vector2Value = ToUV(e.mousePosition); e.Use(); Repaint(); }
                // A button released outside the inspector never sends MouseUp here,
                // and the corner would then keep following the pointer.
                else if (e.type == EventType.MouseUp || e.type == EventType.MouseLeaveWindow
                      || e.type == EventType.Ignore)
                {
                    dragIndex = -1;
                    if (e.type == EventType.MouseUp) e.Use();
                }
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
}
