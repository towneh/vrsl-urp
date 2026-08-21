#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// Shared header drawing helpers used by VRSL custom inspectors — the VRSL
    /// logo, the package version bar, and the composite Draw() that combines
    /// them with spacing.
    /// </summary>
    internal static class VRSL_EditorHeader
    {
        static Texture _logo;
        static bool    _logoSearched;

        const string LogoPath =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/Media/VRStageLighting-Logo.png";
        const string LogoName = "VRStageLighting-Logo";

        /// <summary>Draws the logo + version bar with trailing spacing. Use as the
        /// first call inside an inspector's OnInspectorGUI.</summary>
        public static void Draw()
        {
            DrawLogo();
            DrawVersionBar(GetVersionString());
            EditorGUILayout.Space();
            EditorGUILayout.Space();
        }

        /// <summary>Draws the VRSL logo centered at the top of an inspector.</summary>
        public static void DrawLogo()
        {
            if (_logo == null && !_logoSearched) { _logo = LoadLogo(); _logoSearched = true; }
            // No blank 140-pixel hole where a missing logo would have gone.
            if (_logo == null) return;

            var style = new GUIStyle(EditorStyles.label)
            {
                fixedHeight = 150,
                contentOffset = new Vector2(0f, -2f),
                alignment = TextAnchor.MiddleCenter,
            };
            var rect = GUILayoutUtility.GetRect(300f, 140f, style);
            GUI.Box(rect, _logo, style);
        }

        /// <summary>The logo, by asset path rather than through Resources. The
        /// package carries one copy of it and that copy is not in a Resources
        /// folder, so a Resources lookup finds nothing however it is spelled.
        /// Editor-only code, so the asset database is the right way to ask.</summary>
        static Texture LoadLogo()
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture>(LogoPath);
            if (tex != null) return tex;
            // An embedded or relocated copy of the package still resolves by name.
            foreach (string guid in AssetDatabase.FindAssets($"{LogoName} t:Texture"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != LogoName) continue;
                tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
                if (tex != null) return tex;
            }
            return null;
        }

        /// <summary>Returns the rich-text version string read from VERSION.txt.</summary>
        public static string GetVersionString()
        {
            string versionPath = Path.GetFullPath(
                "Packages/town.mr.vrsl-urp/Runtime/VERSION.txt");

            string version = "?.?.?";
            try
            {
                if (File.Exists(versionPath))
                    version = File.ReadAllText(versionPath).Trim();
            }
            catch
            {
                // Version string is cosmetic — fall back to the placeholder on any I/O error.
            }

            return "VR Stage Lighting ver: <b><color=#b33cff>" + version + "</color></b>";
        }

        /// <summary>Draws a Shuriken-style centered title bar (the purple strip
        /// under the logo). Accepts rich-text; used for the version line.</summary>
        public static void DrawVersionBar(string title)
        {
            var style = new GUIStyle("ShurikenModuleTitle")
            {
                font = new GUIStyle(EditorStyles.boldLabel).font,
                border = new RectOffset(15, 7, 4, 4),
                fontSize = 14,
                fixedHeight = 22,
                contentOffset = new Vector2(0f, -2f),
                alignment = TextAnchor.MiddleCenter,
                richText = true,
            };
            var rect = GUILayoutUtility.GetRect(16f, 22f, style);
            GUI.Box(rect, title, style);
        }
    }
}
#endif
