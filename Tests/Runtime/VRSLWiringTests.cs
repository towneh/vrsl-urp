#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The wiring table against the project it describes.
    ///
    /// Both rows here guard the same failure and it is the one M7 exists to remove: a
    /// table that has quietly stopped matching resolves nothing, and a manager that
    /// resolves nothing looks exactly like a manager nobody wired. Neither produces an
    /// error, so without these the first symptom is a dark rig in somebody's scene.
    /// </summary>
    public class VRSLWiringTests
    {
        [Test]
        public void EveryWiringGuidStillResolvesToAnAssetInThisPackage()
        {
            var missing = new List<string>();
            foreach (var field in VRSLWiring.All)
            {
                var asset = VRSLWiring.Load(field);
                if (asset == null) { missing.Add($"{field.Field} ({field.Guid})"); continue; }

                // Inside this package, not merely somewhere. A GUID that resolved to the
                // legacy package's identically-named copy would load fine and be the
                // precise fault the GUID table exists to prevent.
                string path = VRSLWiring.PathOf(field);
                Assert.That(path, Does.Contain("town.mr.vrsl-urp").Or.StartWith("Assets/"),
                    $"{field.Field} resolved to {path}, which is not this package");
            }

            // Named together rather than one at a time: a GUID regeneration moves every
            // asset at once, and a row that stops at the first is read as one broken
            // field rather than as a table that no longer describes anything.
            Assert.IsEmpty(missing,
                "wiring GUIDs that resolve to nothing — the assets moved, or their GUIDs "
              + "were regenerated, and every manager relying on them now resolves nothing "
              + "at all: " + string.Join(", ", missing));
        }

        [Test]
        public void EveryWiringFieldNameExistsOnItsManager()
        {
            AssertFieldsExist(typeof(VRSL_URPLightManager), VRSLWiring.Dmx, "DMX");
            AssertFieldsExist(typeof(VRSL_AudioLinkURPLightManager), VRSLWiring.AudioLink, "AudioLink");
        }

        /// <summary>
        /// The table addresses fields by name, so a rename that compiles cleanly
        /// everywhere else stops resolution dead and says nothing about it.
        /// </summary>
        static void AssertFieldsExist(System.Type manager, VRSLWiringField[] fields, string which)
        {
            foreach (var field in fields)
            {
                var info = manager.GetField(field.Field,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(info,
                    $"the {which} wiring table names '{field.Field}', which {manager.Name} does "
                  + "not have. A rename compiles everywhere else and leaves this field "
                  + "unresolvable in silence.");
            }
        }

        [Test]
        public void EveryFieldSaysWhatTheAuthorWillSeeRatherThanWhatIsNull()
        {
            foreach (var field in VRSLWiring.All)
            {
                Assert.IsNotEmpty(field.Consequence, $"{field.Field} has no consequence");

                // R-M7-5: the message names what the author sees. A consequence that just
                // repeats the field name is the diagnostic this milestone replaces, and
                // it is easy to write one by accident when adding a field to the table.
                Assert.That(field.Consequence, Does.Not.Contain(field.Field),
                    $"{field.Field}'s consequence restates the field name rather than "
                  + "naming what goes wrong on screen");
                Assert.That(field.Consequence, Does.Not.Contain("null").IgnoreCase,
                    $"{field.Field}'s consequence talks about being null rather than about "
                  + "what the author will see");
            }
        }
    }
}
#endif
