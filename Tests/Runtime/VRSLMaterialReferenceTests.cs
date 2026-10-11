#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// AudioLink isn't a dependency, so the package's assets allowed to reach into it are
    /// named by where they live: a folder called AudioLink or AudioLink-Scenes. Where
    /// AudioLink is installed their references into it must resolve; where it isn't,
    /// nothing in the set can, so rows L2 and L6 leave the set out.
    /// </summary>
    static class VRSLAudioLinkSet
    {
        public const string PackageRoot = "Packages/com.llealloo.audiolink/";

        public static bool Installed =>
            UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.llealloo.audiolink") != null;

        public static bool Contains(string assetPath) =>
            assetPath.StartsWith(VRSLWiring.PackageRoot)
            && (assetPath.Contains("/AudioLink/") || assetPath.Contains("/AudioLink-Scenes/"));
    }

    /// <summary>
    /// What the fixture materials point at, read from the asset files themselves.
    ///
    /// A texture reference to an asset the project doesn't hold loads as nothing, and the
    /// shader samples its default in its place. For an emission mask that default is
    /// white, so the whole fixture body glows and nothing anywhere says why. Read from the
    /// text rather than through <see cref="Material"/>, which can't tell a missing
    /// reference from an empty one.
    /// </summary>
    public class VRSLMaterialReferenceTests
    {
        const string Folder = "Runtime/Materials/Lights";

        static readonly Regex ShaderRef = new Regex(@"m_Shader: \{fileID: (\d+), guid: ([0-9a-f]{32})");
        static readonly Regex TextureRef = new Regex(
            @"- (_\w+):\s*\n\s*m_Texture: \{fileID: -?\d+, guid: ([0-9a-f]{32})");

        // Unity's built-in resources, which no AssetDatabase path names.
        static bool BuiltIn(string guid) => guid.StartsWith("0000000000000000");

        static string Where(string path) => path.Length == 0 ? "no asset" : path;

        [Test]
        public void EveryFixtureMaterialReferenceResolvesInsideThePackage()
        {
            var roots = new List<string> { VRSLWiring.PackageRoot };
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(VRSLWiring.PackageRoot);
            Assert.IsNotNull(info, "this package isn't installed as a package here");
            roots.AddRange(info.dependencies.Select(d => $"Packages/{d.name}/"));

            // AudioLink is optional rather than a dependency: only the AudioLink fixtures'
            // own materials may point into it, and those references still have to resolve
            // there where AudioLink is installed.
            bool audioLinkInstalled = VRSLAudioLinkSet.Installed;
            bool audioLinkAllowed = false;
            bool Inside(string path) =>
                roots.Any(path.StartsWith)
                || (audioLinkAllowed && path.StartsWith(VRSLAudioLinkSet.PackageRoot));

            var materials = AssetDatabase.FindAssets("t:Material", new[] { VRSLWiring.PackageRoot + Folder })
                .Select(AssetDatabase.GUIDToAssetPath).ToList();
            Assert.IsNotEmpty(materials, "the fixture materials weren't found");

            var bad = new List<string>();
            foreach (var path in materials)
            {
                audioLinkAllowed = VRSLAudioLinkSet.Contains(path);
                if (audioLinkAllowed && !audioLinkInstalled) continue;
                string text = File.ReadAllText(Path.GetFullPath(path));
                string name = Path.GetFileNameWithoutExtension(path);

                Shader shader = null;
                var s = ShaderRef.Match(text);
                if (s.Success && !BuiltIn(s.Groups[2].Value))
                {
                    string shaderPath = AssetDatabase.GUIDToAssetPath(s.Groups[2].Value);
                    if (Inside(shaderPath)) shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
                    if (shader == null) { bad.Add($"{name}: shader {s.Groups[2].Value} ({Where(shaderPath)})"); continue; }
                }

                foreach (Match t in TextureRef.Matches(text))
                {
                    string property = t.Groups[1].Value, guid = t.Groups[2].Value;
                    if (BuiltIn(guid)) continue;
                    // A slot the shader no longer declares is never sampled; many carry
                    // upstream references and nothing reads them.
                    if (shader != null && shader.FindPropertyIndex(property) < 0) continue;
                    string texturePath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!Inside(texturePath) || AssetDatabase.LoadAssetAtPath<Texture>(texturePath) == null)
                        bad.Add($"{name}: {property} {guid} ({Where(texturePath)})");
                }
            }

            Assert.IsEmpty(bad,
                "fixture materials pointing at a shader or texture that isn't in this package, one it "
              + "depends on, or (for the AudioLink fixtures only) AudioLink, so the fixture doesn't "
              + "render as authored: "
              + string.Join(", ", bad));
        }
    }
}
#endif
