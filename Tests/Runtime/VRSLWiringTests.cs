#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

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

                // Inside this package, not merely somewhere that loads. A GUID resolving
                // to the legacy package's identically-named copy would load perfectly
                // well and be the precise fault the table exists to prevent, and so
                // would one that had drifted onto a stray asset in the host project.
                string path = VRSLWiring.PathOf(field);
                Assert.That(path, Does.StartWith(VRSLWiring.PackageRoot),
                    $"{field.Field} resolved to '{path}', which is not inside "
                  + VRSLWiring.PackageRoot);
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
        public void ResolveFillsEmptyFieldsAndLeavesAnAuthorsOwnAlone()
        {
            var go = new GameObject("wiring row");
            go.SetActive(false);   // no Awake, so the manager never claims the singleton
            try
            {
                var manager = go.AddComponent<VRSL_URPLightManager>();

                // A deliberate override: the wrong asset for this field, which is exactly
                // what R-M7-3 protects. Resolution that "knows better" would replace it,
                // and an author who set something unusual on purpose would find it
                // silently undone.
                var somebodyElses = VRSLWiring.Load(FieldNamed("dmxSpinTimerTexture"));
                manager.dmxMainTexture = (RenderTexture)somebodyElses;

                var result = VRSLWiring.Resolve(manager);

                Assert.AreSame(somebodyElses, manager.dmxMainTexture,
                    "resolution overwrote a field the author had set");
                Assert.That(result.Filled.Select(f => f.Field),
                    Does.Not.Contain("dmxMainTexture"),
                    "a field that was already set was reported as filled");

                // Everything else was empty and should now be the package's own asset.
                Assert.IsEmpty(result.Unresolved,
                    "nothing should be unresolvable in a project that has this package: "
                  + string.Join(", ", result.Unresolved.Select(f => f.Field)));
                Assert.AreEqual(VRSLWiring.Dmx.Length - 1, result.Filled.Count,
                    "every empty field should have been filled exactly once");
                foreach (var field in VRSLWiring.Dmx)
                    Assert.IsNotNull(new SerializedObject(manager).FindProperty(field.Field)
                                        .objectReferenceValue,
                        $"{field.Field} is still empty after a resolve");

                // Idempotent: a second pass has nothing left to do, so a repair an author
                // presses twice does not report ten changes it did not make.
                var again = VRSLWiring.Resolve(manager);
                Assert.IsFalse(again.ChangedAnything, "a second resolve changed something");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void TheInnerOfTwoHoldsDoesNotReleaseTheOuter()
        {
            var go = new GameObject("hold row");
            go.SetActive(false);
            try
            {
                var manager = go.AddComponent<VRSL_URPLightManager>();
                Assert.IsFalse(VRSLWiring.AutoResolveSuppressed(manager));

                var outer = VRSLWiring.SuppressAutoResolve(manager);
                using (VRSLWiring.SuppressAutoResolve(manager))
                    Assert.IsTrue(VRSLWiring.AutoResolveSuppressed(manager));

                // The claim. Held as a set rather than a count, the inner scope closing
                // would have released the outer one's hold too — and the next bounce
                // would refill a field the outer holder had emptied on purpose, which is
                // how quality Off comes back on while its counters say it is gone.
                Assert.IsTrue(VRSLWiring.AutoResolveSuppressed(manager),
                    "the inner scope released the outer scope's hold");

                outer.Dispose();
                Assert.IsFalse(VRSLWiring.AutoResolveSuppressed(manager),
                    "the last hold did not release");

                // Disposing twice must not drop a hold it no longer owns.
                outer.Dispose();
                using (VRSLWiring.SuppressAutoResolve(manager))
                    Assert.IsTrue(VRSLWiring.AutoResolveSuppressed(manager),
                        "a double dispose released somebody else's hold");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [UnityTest]
        public IEnumerator AManagerHandedTheSingletonResolvesItsOwnWiring()
        {
            // Two managers in a loaded scene: the first owns the singleton, the second
            // stands by unwired. Disabling the owner hands ownership over by calling the
            // standby's TakeOwnership directly, which never goes through OnEnable — so a
            // resolver hooked there would leave this manager building its materials and
            // passes from empty fields, which is the whole failure the milestone is about.
            var ownerGo   = new GameObject("wiring owner");
            var standbyGo = new GameObject("wiring standby");
            try
            {
                var owner = ownerGo.AddComponent<VRSL_URPLightManager>();
                yield return null;

                // Added while switched off, because Awake destroys an enabled duplicate
                // outright: `if (Instance != null && Instance != this) Destroy(this)`.
                // A component that is off when Awake runs returns early instead and
                // survives, and switching it on afterwards leaves it enabled and not the
                // owner — which is the only state a handover has anything to hand to.
                standbyGo.SetActive(false);
                var standby = standbyGo.AddComponent<VRSL_URPLightManager>();
                standby.enabled = false;
                standbyGo.SetActive(true);
                standby.enabled = true;
                yield return null;
                Assert.IsTrue(standby != null, "the standby manager destroyed itself");

                // Emptied after it came up, so the standby is genuinely unwired at the
                // moment ownership arrives.
                var so = new SerializedObject(standby);
                foreach (var field in VRSLWiring.Dmx)
                    so.FindProperty(field.Field).objectReferenceValue = null;
                so.ApplyModifiedProperties();
                Assert.IsNotEmpty(VRSLWiring.Empty(standby), "the standby should start unwired");

                owner.enabled = false;
                yield return null;

                Assert.IsEmpty(VRSLWiring.Empty(standby).Select(f => f.Field),
                    "a manager handed the singleton set itself up without resolving its "
                  + "wiring first, so its materials and passes were built from empty fields");
            }
            finally
            {
                Object.DestroyImmediate(standbyGo);
                Object.DestroyImmediate(ownerGo);
            }
        }

        static VRSLWiringField FieldNamed(string name)
        {
            foreach (var f in VRSLWiring.Dmx) if (f.Field == name) return f;
            throw new AssertionException($"no wiring field called {name}");
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
