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
    /// The scripts the shipped prefabs and scenes carry, read from the asset files.
    ///
    /// A component whose script isn't in the project arrives in every scene that places the
    /// prefab as a missing script. Scripts from VRChat's SDK are the usual case, and so are
    /// scripts whose whole body compiles only under UdonSharp: their file resolves, their
    /// class doesn't exist. Only this package and the packages it depends on count, so a
    /// script the host project happens to have doesn't pass for one every project has.
    /// </summary>
    public class VRSLScriptReferenceTests
    {
        static readonly Regex ScriptRef = new Regex(@"m_Script: \{fileID: (-?\d+), guid: ([0-9a-f]{32})");

        [Test]
        public void EveryShippedPrefabAndSceneScriptLoads()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(VRSLWiring.PackageRoot);
            Assert.IsNotNull(info, "this package isn't installed as a package here");

            // Dependencies of dependencies count: URP's Volume lives in the core package URP brings.
            var roots = new List<string> { VRSLWiring.PackageRoot };
            var seen = new HashSet<string> { info.name };
            var pending = new Queue<UnityEditor.PackageManager.PackageInfo>(new[] { info });
            while (pending.Count > 0)
            {
                foreach (var d in pending.Dequeue().dependencies)
                {
                    if (!seen.Add(d.name)) continue;
                    roots.Add($"Packages/{d.name}/");
                    var dep = UnityEditor.PackageManager.PackageInfo.FindForPackageName(d.name);
                    if (dep != null) pending.Enqueue(dep);
                }
            }

            bool Inside(string path) => roots.Any(path.StartsWith);

            var folder = new[] { VRSLWiring.PackageRoot.TrimEnd('/') };
            var assets = AssetDatabase.FindAssets("t:Prefab", folder)
                .Concat(AssetDatabase.FindAssets("t:Scene", folder))
                .Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();
            Assert.That(assets.Count, Is.GreaterThan(10), "the shipped prefabs and scenes weren't found");

            var resolved = new Dictionary<(string, long), bool>();
            var bad = new Dictionary<string, int>();
            foreach (var path in assets)
            {
                string text = File.ReadAllText(Path.GetFullPath(path));
                foreach (Match m in ScriptRef.Matches(text))
                {
                    var key = (m.Groups[2].Value, long.Parse(m.Groups[1].Value));
                    if (!resolved.TryGetValue(key, out bool ok))
                        resolved[key] = ok = Loads(key.Item1, key.Item2, Inside);
                    if (ok) continue;
                    string entry = $"{Path.GetFileName(path)} ({key.Item1})";
                    bad.TryGetValue(entry, out int n);
                    bad[entry] = n + 1;
                }
            }

            Assert.IsEmpty(bad,
                "shipped prefabs and scenes carrying a script that doesn't load from this package or one it "
              + "depends on, so every instance arrives with a missing script: "
              + string.Join(", ", bad.Select(b => $"{b.Key} x{b.Value}")));
        }

        static bool Loads(string guid, long fileId, System.Func<string, bool> inside)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || !inside(path)) return false;
            // 11500000 is a script file's own MonoScript; any other id is a class inside a DLL.
            foreach (var script in AssetDatabase.LoadAllAssetsAtPath(path).OfType<MonoScript>())
            {
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out _, out long local)) continue;
                if (local == fileId) return script.GetClass() != null;
            }
            return false;
        }
    }
}
#endif
