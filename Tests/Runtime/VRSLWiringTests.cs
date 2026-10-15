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
            AssertFieldsExist(typeof(VRSL_DMXURPLightManager), VRSLWiring.Dmx, "DMX");
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
            var somebodyElses = ScriptableObject.CreateInstance<VRSLGridTextures>();
            try
            {
                var manager = go.AddComponent<VRSL_DMXURPLightManager>();

                // A deliberate override: an author's own grid textures rather than the
                // package's, which is exactly what never filling a set field protects. Resolution that
                // "knows better" would replace it, and an author who set something on
                // purpose would find it silently undone.
                Assert.IsFalse(FieldNamed("gridTextures").Optional,
                    "this row subtracts one from the mandatory count for the overridden "
                  + "field, so it fails by one if that field is ever made optional - and "
                  + "the failure would name resolution rather than the table");
                manager.gridTextures = somebodyElses;

                var result = VRSLWiring.Resolve(manager);

                Assert.AreSame(somebodyElses, manager.gridTextures,
                    "resolution overwrote a field the author had set");
                Assert.That(result.Filled.Select(f => f.Field),
                    Does.Not.Contain("gridTextures"),
                    "a field that was already set was reported as filled");

                // Everything else was empty and should now be the package's own asset.
                Assert.IsEmpty(result.Unresolved,
                    "nothing should be unresolvable in a project that has this package: "
                  + string.Join(", ", result.Unresolved.Select(f => f.Field)));
                // The mandatory ones, minus the overridden field. The three that document
                // a meaning when empty are not filled unless somebody asks.
                int expected = VRSLWiring.Dmx.Count(f => !f.Optional) - 1;
                Assert.AreEqual(expected, result.Filled.Count,
                    "every empty field that should be filled was not filled exactly once");
                Assert.IsEmpty(Required(VRSLWiring.Empty(manager)),
                    "a mandatory field is still empty after a resolve");

                // And asking fills the rest, which is what the repair button does.
                var asked = VRSLWiring.Resolve(manager, includeOptional: true);
                Assert.IsNotEmpty(asked.Filled, "asking did not fill the optional fields");
                Assert.IsEmpty(VRSLWiring.Empty(manager).Select(f => f.Field),
                    "something was still empty after an explicit repair");

                // Idempotent: a second pass has nothing left to do, so a repair an author
                // presses twice does not report changes it did not make.
                var again = VRSLWiring.Resolve(manager);
                Assert.IsFalse(again.ChangedAnything, "a second resolve changed something");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(somebodyElses);
            }
        }

        [Test]
        public void TheInnerOfTwoHoldsDoesNotReleaseTheOuter()
        {
            var go = new GameObject("hold row");
            go.SetActive(false);
            try
            {
                var manager = go.AddComponent<VRSL_DMXURPLightManager>();
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
            // passes from empty fields, which is the whole failure self-wiring exists to prevent.
            var ownerGo   = new GameObject("wiring owner");
            var standbyGo = new GameObject("wiring standby");
            try
            {
                var owner = ownerGo.AddComponent<VRSL_DMXURPLightManager>();
                yield return null;

                // Added while switched off, because Awake destroys an enabled duplicate
                // outright: `if (Instance != null && Instance != this) Destroy(this)`.
                // A component that is off when Awake runs returns early instead and
                // survives, and switching it on afterwards leaves it enabled and not the
                // owner — which is the only state a handover has anything to hand to.
                standbyGo.SetActive(false);
                var standby = standbyGo.AddComponent<VRSL_DMXURPLightManager>();
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

                Assert.IsEmpty(Required(VRSLWiring.Empty(standby)),
                    "a manager handed the singleton set itself up without resolving its "
                  + "wiring first, so its materials and passes were built from empty fields");
            }
            finally
            {
                Object.DestroyImmediate(standbyGo);
                Object.DestroyImmediate(ownerGo);
            }
        }

        [UnityTest]
        public IEnumerator EmptyingAFieldInTheInspectorFillsItBackIn()
        {
            var go = new GameObject("validate row");
            go.SetActive(false);
            try
            {
                var manager = go.AddComponent<VRSL_DMXURPLightManager>();
                var so = new SerializedObject(manager);
                foreach (var field in VRSLWiring.Dmx)
                    so.FindProperty(field.Field).objectReferenceValue = null;
                so.ApplyModifiedProperties();

                // What the inspector does after an edit. The object stays inactive, so
                // nothing here is the enable path doing the work instead.
                VRSLWiring.ResolveOnValidate(manager);

                // The mandatory subset, not every field: the optional three are never
                // filled by this path, so judging all six would leave the assertion
                // non-empty whether or not the resolve was deferred.
                Assert.IsNotEmpty(Required(VRSLWiring.Empty(manager)),
                    "resolution ran during OnValidate rather than after it, which is where "
                  + "import and mid-edit serialisation live");

                // The next editor tick, driven rather than waited for: delayCall does
                // not fire inside a batch-mode play-mode test, so waiting on it would
                // test Unity's scheduler and nothing of ours.
                VRSLWiring.FlushPending(manager);
                yield return null;

                Assert.IsEmpty(Required(VRSLWiring.Empty(manager)),
                    "emptying a field in the inspector left it empty");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// W7. An override survives the paths that resolution now runs on.
        ///
        /// Entering play mode enables the manager, and leaving it enables the edit-mode
        /// one again — so "survives a play-mode round trip" is, mechanically, "survives
        /// the enable path and a deferred validate". Both run resolution, both are
        /// exercised here, and a partial repair is the realistic case: other fields empty
        /// and needing filling, one field set deliberately and not to be touched.
        ///
        /// The round trip proper stays a hand row. A PlayMode test is already in play
        /// mode and cannot leave it, and Unity discards play-mode scene edits on exit
        /// regardless, so the thing a test can honestly assert is what is asserted here.
        /// </summary>
        [UnityTest]
        public IEnumerator AnOverrideSurvivesEveryPathThatResolves()
        {
            var go = new GameObject("override row");
            go.SetActive(false);
            var somebodyElses = ScriptableObject.CreateInstance<VRSLGridTextures>();
            try
            {
                var manager = go.AddComponent<VRSL_DMXURPLightManager>();

                // Deliberately not the package's asset, and deliberately alone in being
                // set: everything else is empty and wants filling, which is what a repair
                // on a real scene looks like.
                var so = new SerializedObject(manager);
                foreach (var field in VRSLWiring.Dmx)
                    so.FindProperty(field.Field).objectReferenceValue = null;
                so.FindProperty("gridTextures").objectReferenceValue = somebodyElses;
                so.ApplyModifiedProperties();

                // The enable path, which is what entering and leaving play mode runs.
                go.SetActive(true);
                yield return null;
                Assert.AreSame(somebodyElses, manager.gridTextures,
                    "enabling the manager overwrote a field the author had set");

                // Checked here rather than only at the end. Asserting once, after both
                // paths, cannot tell which of them did the work — remove the enable hook
                // and the validate pass below would fill everything and the row would
                // still pass, while the path it names went untested.
                Assert.IsEmpty(Required(VRSLWiring.Empty(manager)),
                    "enabling the manager did not fill the fields that were empty");

                // Emptied again so the validate path has something of its own to do, and
                // never the overridden field: the claim is that a repair leaves it alone
                // while repairing around it.
                var other = VRSLWiring.Dmx.First(f => f.Field != "gridTextures" && !f.Optional);
                var again = new SerializedObject(manager);
                again.FindProperty(other.Field).objectReferenceValue = null;
                again.ApplyModifiedProperties();

                // And the validate path, deferred, which is what an inspector edit runs.
                VRSLWiring.ResolveOnValidate(manager);
                Assert.IsNotEmpty(Required(VRSLWiring.Empty(manager)),
                    "validate resolved synchronously rather than queueing");
                VRSLWiring.FlushPending(manager);
                yield return null;

                Assert.AreSame(somebodyElses, manager.gridTextures,
                    "a validate pass overwrote a field the author had set");
                Assert.IsEmpty(Required(VRSLWiring.Empty(manager)),
                    $"a validate pass did not fill {other.Field} back in");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(somebodyElses);
            }
        }

        /// <summary>
        /// W8. Resolution is by identity, so an identically-named asset elsewhere in
        /// the project cannot be picked instead.
        ///
        /// The requirement is about coexisting with <c>com.acchosen.vr-stage-lighting</c>,
        /// which ships its decode textures under exactly this package's names. Rather than gate
        /// this on that package being installed — it is not, here, so the row would
        /// compile out and prove nothing — it stages the fault itself: a decoy asset with
        /// the same name, which makes a name search genuinely ambiguous. That is the
        /// mechanism under test, and staging it is more controlled than installing a
        /// package and hoping it collides.
        /// </summary>
        [Test]
        public void AnIdenticallyNamedAssetElsewhereIsNotPickedInstead()
        {
            var field = FieldNamed("gridTextures");
            string wanted = VRSLWiring.PathOf(field);
            string name   = System.IO.Path.GetFileNameWithoutExtension(wanted);

            // A name nothing else can already be using. A fixed one risks colliding with
            // a folder somebody already has, and the cleanup below deletes the folder
            // whole — which would take their assets with it. This row is not worth
            // anybody losing work over.
            string leaf   = "VRSL-WiringDecoy-" + System.Guid.NewGuid().ToString("N");
            string folder = "Assets/" + leaf;
            string decoyPath = $"{folder}/{name}.asset";
            bool mine = false;
            try
            {
                Assert.IsFalse(AssetDatabase.IsValidFolder(folder), "the decoy folder already exists");
                AssetDatabase.CreateFolder("Assets", leaf);
                mine = AssetDatabase.IsValidFolder(folder);
                Assert.IsTrue(mine, "could not create the decoy folder");

                var decoy = ScriptableObject.CreateInstance<VRSLGridTextures>();
                decoy.name = name;
                AssetDatabase.CreateAsset(decoy, decoyPath);
                AssetDatabase.Refresh();

                // The row is worthless unless the decoy really does collide, so check
                // that first: a name search has to be ambiguous now, or resolution had
                // nothing to get wrong and passing means nothing.
                var byName = AssetDatabase.FindAssets($"\"{name}\"");
                Assert.Greater(byName.Length, 1,
                    "the decoy did not collide by name, so this row proves nothing");

                var resolved = VRSLWiring.Load(field);
                Assert.IsNotNull(resolved, "resolution found nothing at all");
                Assert.AreEqual(wanted, AssetDatabase.GetAssetPath(resolved),
                    "resolution picked an identically-named asset from outside this "
                  + "package — which is what a project with the legacy package installed "
                  + "looks like, and a manager wired to its decode chain reports green on "
                  + "every diagnostic while every channel reads zero");
            }
            finally
            {
                // Only the folder this row made. Deleting one it merely found would take
                // whatever else was in it.
                if (mine)
                {
                    AssetDatabase.DeleteAsset(decoyPath);
                    AssetDatabase.DeleteAsset(folder);
                    AssetDatabase.Refresh();
                }
            }
        }

        /// <summary>The names of the fields that must never be left empty. The optional
        /// four are a choice, so a row asserting "nothing is empty" would be asserting
        /// that the choice cannot be made.</summary>
        static IEnumerable<string> Required(IEnumerable<VRSLWiringField> fields) =>
            fields.Where(f => !f.Optional).Select(f => f.Field);

        static VRSLWiringField FieldNamed(string name)
        {
            foreach (var f in VRSLWiring.Dmx) if (f.Field == name) return f;
            throw new AssertionException($"no wiring field called {name}");
        }

        /// <summary>
        /// W13. Every slot of the shipped Grid Textures holds its own layout's texture.
        /// The strobe timers are the ones worth a row: 0.2.0-rc.6's Vertical and Legacy
        /// managers both pointed at the Horizontal timer, so their strobes took their
        /// phase from a grid nothing wrote to, and a slip of the same kind here would look
        /// exactly as healthy.
        /// </summary>
        [Test]
        public void TheShippedGridTexturesHoldEachLayoutsOwnTextures()
        {
            // Main, movement, strobe, strobe timer, spin timer, as each layout ships them.
            var expected = new Dictionary<string, string[]>
            {
                ["horizontal"] = new[]
                {
                    "555ce9e464c9a54c9d331e0030c258ef", "63c403376634fef0560b44d7f19aedaa",
                    "e3fa773f91559868d4104ab3ce54bf79", "e6641cd38ad1a21be3425ab43ecc2506",
                    "5bee9c07b3305e88be89de0d01fc2f07",
                },
                ["vertical"] = new[]
                {
                    "78c70f9c712bd81bf5c13a40970fd79b", "c6d6373129a6cae52b02e2b675ad7e86",
                    "6f30f578d96c57d49dc9297f12e7fb0b", "954ba27aa605bf10c6625c64c834303f",
                    "da695f1470322a477c370746a7a2571f",
                },
                ["legacy"] = new[]
                {
                    "17f83f063191dbffbaae01943cc9c235", "015583c6b281a3ff6b1af6d872ac4610",
                    "a928e074dadf56e05191a31cd701f480", "66a7e6c0f0eebb9378d52ca3820d9ccb",
                    "a997187a06df0deca7f350bc32369b8f",
                },
            };

            var asset = VRSLWiring.Load(FieldNamed("gridTextures")) as VRSLGridTextures;
            Assert.IsNotNull(asset, "the shipped Grid Textures asset is not in this project");

            static string GuidOf(Object o) => o == null
                ? "(empty)"
                : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(o));

            var wrong = new List<string>();
            foreach (var (layout, set) in new[]
                     {
                         ("horizontal", asset.horizontal), ("vertical", asset.vertical),
                         ("legacy", asset.legacy),
                     })
            {
                var slots = new (string Name, Object Texture)[]
                {
                    ("main", set.main), ("movement", set.movement), ("strobe", set.strobe),
                    ("strobeTimer", set.strobeTimer), ("spinTimer", set.spinTimer),
                };
                for (int i = 0; i < slots.Length; i++)
                    if (GuidOf(slots[i].Texture) != expected[layout][i])
                        wrong.Add($"{layout}.{slots[i].Name} holds {GuidOf(slots[i].Texture)}, "
                                + $"expected {expected[layout][i]}");
            }
            Assert.IsEmpty(wrong, string.Join("; ", wrong));
        }

        [Test]
        public void EveryFieldSaysWhatTheAuthorWillSeeRatherThanWhatIsNull()
        {
            foreach (var field in VRSLWiring.All)
            {
                Assert.IsNotEmpty(field.Consequence, $"{field.Field} has no consequence");

                // The message names what the author sees. A consequence that just repeats
                // the field name is the kind of diagnostic the table exists to replace, and
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
