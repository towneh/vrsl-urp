#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The migration tool's table against the package it points into.
    ///
    /// The tool itself compiles only beside the legacy package, which no test project has,
    /// so a table that has stopped matching the package would only show up in an author's
    /// scene: fixtures passed over in silence, or migrated onto a prefab that isn't there.
    /// </summary>
    public class VRSLMigrationTableTests
    {
        const string SupersededFolder = VRSLWiring.PackageRoot + "Runtime/Prefabs/DMX/Superseded/";

        static bool Superseded(string path) => path.StartsWith(SupersededFolder);

        [Test]
        public void EveryUpstreamGuidAppearsOnce()
        {
            var all = VRSLMigrationTable.Pairs.Select(p => p.UpstreamGuid)
                .Concat(VRSLMigrationTable.Unported.Keys);
            var repeated = all.GroupBy(g => g).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.IsEmpty(repeated,
                "upstream prefabs listed twice, so one of the entries is never reached: "
              + string.Join(", ", repeated));
        }

        [Test]
        public void EveryUrpGuidIsAPrefabInThisPackage()
        {
            var bad = new List<string>();
            foreach (var pair in VRSLMigrationTable.Pairs)
            {
                foreach (var guid in new[] { pair.UrpGuid, pair.Rc6UrpGuid })
                {
                    if (guid == null) continue;
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!path.StartsWith(VRSLWiring.PackageRoot) || !path.EndsWith(".prefab"))
                        bad.Add($"{pair.FriendlyName} -> {guid} ('{path}')");
                }
            }
            Assert.IsEmpty(bad,
                "migration targets that are not a prefab in this package; the tool skips "
              + "those fixtures with a warning: " + string.Join(", ", bad));
        }

        [Test]
        public void NoPairProducesASupersededPrefab()
        {
            var superseded = new List<string>();
            foreach (var pair in VRSLMigrationTable.Pairs)
            {
                string path = AssetDatabase.GUIDToAssetPath(pair.UrpGuid);
                if (Superseded(path)) superseded.Add($"{pair.FriendlyName} -> {path}");
            }
            Assert.IsEmpty(superseded,
                "fixture prefabs serve every grid layout, so every upstream fixture migrates "
              + "onto the one prefab: " + string.Join(", ", superseded));
        }

        [Test]
        public void EverySupersededPrefabHasAReplacementOutsideTheFolder()
        {
            var inFolder = AssetDatabase.FindAssets("t:Prefab", new[] { SupersededFolder.TrimEnd('/') })
                .Select(AssetDatabase.GUIDToAssetPath).ToList();
            Assert.IsNotEmpty(inFolder, "the superseded prefabs weren't found");

            var bad = new List<string>();
            foreach (var path in inFolder)
            {
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (!VRSLMigrationTable.Superseded.TryGetValue(guid, out string replacement))
                    bad.Add($"{path} has no replacement");
                else
                {
                    string to = AssetDatabase.GUIDToAssetPath(replacement);
                    if (!to.StartsWith(VRSLWiring.PackageRoot) || !to.EndsWith(".prefab") || Superseded(to))
                        bad.Add($"{path} -> '{to}'");
                }
            }
            foreach (var guid in VRSLMigrationTable.Superseded.Keys)
                if (!Superseded(AssetDatabase.GUIDToAssetPath(guid)))
                    bad.Add($"{guid} is listed as superseded but isn't in the folder");
            Assert.IsEmpty(bad,
                "superseded prefabs the replace menu can't move a scene off: " + string.Join(", ", bad));
        }

        [Test]
        public void EachRc6TargetIsTheOldPerLayoutPrefab()
        {
            var pairs = VRSLMigrationTable.Pairs.Where(p => p.Rc6UrpGuid != null).ToList();
            Assert.IsNotEmpty(pairs);
            foreach (var pair in pairs)
            {
                Assert.AreNotEqual(pair.UrpGuid, pair.Rc6UrpGuid, pair.FriendlyName);
                Assert.That(Superseded(AssetDatabase.GUIDToAssetPath(pair.Rc6UrpGuid)),
                    $"{pair.FriendlyName}: its rc.6 target should be a superseded prefab");
            }
        }
    }
}
#endif
