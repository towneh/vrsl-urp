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
                string path = AssetDatabase.GUIDToAssetPath(pair.UrpGuid);
                if (!path.StartsWith(VRSLWiring.PackageRoot) || !path.EndsWith(".prefab"))
                    bad.Add($"{pair.FriendlyName} -> {pair.UrpGuid} ('{path}')");
            }
            Assert.IsEmpty(bad,
                "migration targets that are not a prefab in this package; the tool skips "
              + "those fixtures with a warning: " + string.Join(", ", bad));
        }

    }
}
#endif
