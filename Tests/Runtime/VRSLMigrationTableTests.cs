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
                .Concat(VRSLMigrationTable.Unported.Keys)
                .Concat(VRSLMigrationTable.LightStencils.Keys);
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

        /// <summary>
        /// L9. The table knows upstream's light stencil assets by GUID (the same from v2.3.0 to
        /// upstream's current release), and the summary the tool shows names each one found with
        /// its count, says what to do instead, and says nothing when there are none.
        /// </summary>
        [Test]
        public void L9_the_table_knows_upstreams_light_stencils_and_the_summary_names_them()
        {
            var upstream = new[]
            {
                "92edb46363dfd9149a96fcecb5b6d192", // Materials/Other/Stencils/VRSL-LightStencil.mat
                "c813d92472af4434ca4f1a05d4703ae3", // Materials/Other/Stencils/VRSL-LightRestorationStencil.mat
                "e17c4816fe62ef5448f6c0cfbdb8e08b", // Shaders/Misc/VRSL-LightStencil.shader
                "c61102b627802b54583713cccdc095b8", // Shaders/Misc/VRSL-LightResotrationStencil.shader
                "29158ac9808b32541ba787435bdfa109", // Prefabs/Stencil Masks/VRSL-StencilMask-Capsule.prefab
                "14fd9d71e91903d4eb44d5289d78eba4", // Prefabs/Stencil Masks/VRSL-StencilMask-Cube.prefab
                "de5b252ed1e43b14a9f655ac98144fff", // Prefabs/Stencil Masks/VRSL-StencilMask-Cylinder.prefab
                "fa0972d9c823bcf4293febe61ff79da6", // Prefabs/Stencil Masks/VRSL-StencilMask-Sphere.prefab
            };
            CollectionAssert.AreEquivalent(upstream, VRSLMigrationTable.LightStencils.Keys,
                "the table must know each of upstream's light stencil assets, so the tool can name them in a scene");
            Assert.AreEqual(upstream.Length, VRSLMigrationTable.LightStencils.Values.Distinct().Count(),
                "two stencil assets share a name, so the summary would add their counts together");

            Assert.IsEmpty(VRSLMigrationTable.LightStencilSummary(new Dictionary<string, int>()),
                "with no light stencil in the scene the summary must say nothing about them");

            var found = VRSLMigrationTable.LightStencils.Values.Select((name, i) => (name, count: i + 1))
                .ToDictionary(f => f.name, f => f.count);
            string summary = VRSLMigrationTable.LightStencilSummary(found);
            foreach (var f in found)
                StringAssert.Contains($"{f.Key}: {f.Value}", summary, "the summary must name each stencil found and count it");
            foreach (var word in new[] { "range", "cone", "wall" })
                StringAssert.Contains(word, summary,
                    "the summary must say how to keep light off an area without a stencil: placement, range and "
                  + "the cone, and that a wall doesn't stop the light");
        }
    }
}
#endif
