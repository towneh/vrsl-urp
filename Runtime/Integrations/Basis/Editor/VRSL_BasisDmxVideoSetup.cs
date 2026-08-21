using UnityEditor;
using UnityEngine;

namespace VRSL.URP.BasisIntegration
{
    /// <summary>
    /// Adds and frames a <see cref="BasisVideoRenderTextureOutput"/> for a
    /// horizontal-mode grid, so the one part of the setup that cannot be worked
    /// out by eye is not left to be worked out by eye.
    ///
    /// The framing is a transpose, not a rotation. The RAW grid RT is 13 cells
    /// wide with each row a fixture; a horizontal grid node's picture is 120
    /// cells wide with each column a fixture, so the column index and the row
    /// index swap. A rotation differs from that by a mirror and still fills the
    /// RT and still lights the rig, reading every channel off the wrong fixture.
    /// Vertical mode is numbered the way the RT is and wants no transpose at all,
    /// which is why the component's own defaults are the identity mapping and
    /// this is a menu item rather than a default.
    ///
    /// The corners set here transpose the whole frame, which is right as it
    /// stands for a stream carrying nothing but the grid. Where the grid is a
    /// strip inside a larger picture, drag the strip's edges afterwards: the
    /// crop belongs to whoever composed the stream and cannot be guessed here.
    /// </summary>
    static class VRSL_BasisDmxVideoSetup
    {
        const string Menu = "VRSL/URP/Add Basis DMX Video Output (Horizontal)";
        const string RawGridRT =
            "Packages/town.mr.vrsl-urp/Runtime/Textures/RTs/DMXRTViewer-RAWValues-Horizontal.renderTexture";
        const string RawGridName = "DMXRTViewer-RAWValues-Horizontal";

        [MenuItem(Menu, true, 402)]
        static bool Validate() => Selection.activeGameObject != null;

        [MenuItem(Menu, false, 402)]
        static void AddAndFrame()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            var target = LoadRawGrid();
            if (target == null)
            {
                EditorUtility.DisplayDialog("VRSL",
                    $"Could not find {RawGridName}. It ships with the package as the RAW "
                  + "DMX grid RenderTexture the decode chain reads.", "OK");
                return;
            }

            var output = go.GetComponent<BasisVideoRenderTextureOutput>();
            bool added = output == null;
            if (added) output = Undo.AddComponent<BasisVideoRenderTextureOutput>(go);
            else Undo.RecordObject(output, "Frame Basis DMX Video Output");

            var so = new SerializedObject(output);
            so.FindProperty("Target").objectReferenceValue = target;

            // A full-frame transpose: the destination's bottom edge reads up the
            // picture's left edge, and its left edge reads along the bottom.
            Set(so, "uvBL", new Vector2(0f, 0f));
            Set(so, "uvBR", new Vector2(0f, 1f));
            Set(so, "uvTR", new Vector2(1f, 1f));
            Set(so, "uvTL", new Vector2(1f, 0f));

            var blit = so.FindProperty("blitShader");
            if (blit.objectReferenceValue == null)
                blit.objectReferenceValue = Shader.Find("Hidden/VRSL-URP/BasisVideoUVBlit");

            var player = so.FindProperty("Player");
            if (player.objectReferenceValue == null)
                player.objectReferenceValue = go.GetComponentInParent<BasisMediaPlayer>();

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(output);

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(output);

            if (player.objectReferenceValue == null)
                Debug.LogWarning($"[VRSL] {(added ? "Added" : "Framed")} the DMX video output on "
                               + $"\"{go.name}\", but found no BasisMediaPlayer on it or above it. "
                               + "Assign one before playing.", output);
            else
                Debug.Log($"[VRSL] {(added ? "Added" : "Framed")} the DMX video output on "
                        + $"\"{go.name}\", transposed for a horizontal grid. If the grid is a "
                        + "strip inside a larger frame rather than the whole picture, drag the "
                        + "strip's edges in the inspector.", output);
        }

        static void Set(SerializedObject so, string field, Vector2 value)
            => so.FindProperty(field).vector2Value = value;

        static RenderTexture LoadRawGrid()
        {
            var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(RawGridRT);
            if (rt != null) return rt;
            // An embedded or relocated copy of the package still resolves by name.
            foreach (var guid in AssetDatabase.FindAssets($"{RawGridName} t:RenderTexture"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) != RawGridName) continue;
                rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
                if (rt != null) return rt;
            }
            return null;
        }
    }
}
