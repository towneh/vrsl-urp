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
        static readonly string[] PerLayoutFolders = { "/Legacy Mode/", "/Vertical Mode/" };

        static bool InPerLayoutFolder(string path) => PerLayoutFolders.Any(path.Contains);

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
        public void NoPairProducesAPerLayoutPrefab()
        {
            var perLayout = new List<string>();
            foreach (var pair in VRSLMigrationTable.Pairs)
            {
                if (pair.UrpGuid == VRSLMigrationTable.VerticalExampleSurface) continue;
                string path = AssetDatabase.GUIDToAssetPath(pair.UrpGuid);
                if (InPerLayoutFolder(path)) perLayout.Add($"{pair.FriendlyName} -> {path}");
            }
            Assert.IsEmpty(perLayout,
                "fixture prefabs serve every grid layout, so a Legacy or Vertical fixture "
              + "migrates onto the Horizontal prefab: " + string.Join(", ", perLayout));

            // The exception is a real per-layout prefab, or it excuses nothing.
            Assert.That(AssetDatabase.GUIDToAssetPath(VRSLMigrationTable.VerticalExampleSurface),
                Does.Contain("/Vertical Mode/"));
        }

        [Test]
        public void EachRc6TargetIsTheOldPerLayoutPrefab()
        {
            var pairs = VRSLMigrationTable.Pairs.Where(p => p.Rc6UrpGuid != null).ToList();
            Assert.IsNotEmpty(pairs);
            foreach (var pair in pairs)
            {
                Assert.AreNotEqual(pair.UrpGuid, pair.Rc6UrpGuid, pair.FriendlyName);
                Assert.That(InPerLayoutFolder(AssetDatabase.GUIDToAssetPath(pair.Rc6UrpGuid)),
                    $"{pair.FriendlyName}: its rc.6 target should be a Legacy or Vertical prefab");
            }
        }
    }
}
#endif
