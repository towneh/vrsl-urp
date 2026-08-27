// Editor-only in substance: resolution reads the AssetDatabase, which a player has no
// equivalent of, and R-M7-1 wants the resolved reference serialised so a built player
// never looks anything up. It lives in the runtime assembly regardless, because the suite
// reaches it and the test assembly does not reference the editor one — the same
// arrangement VRSLBenchmarkScene uses for the same reason.
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// One field that has exactly one correct value, and what an author sees when it
    /// does not have it.
    /// </summary>
    class VRSLWiringField
    {
        /// <summary>The serialised field on the manager. Pinned by a row, because a
        /// rename here stops resolution silently — which is the failure this whole
        /// mechanism exists to remove, arriving through its own front door.</summary>
        public string Field;

        /// <summary>This package's asset, by GUID.</summary>
        public string Guid;

        /// <summary>
        /// What the author will see when the field is empty.
        ///
        /// The consequence, not the cause. "Every surface lights as flat grey" is the
        /// register R-M7-5 asks for; "surfacePropertiesShader is null" is the thing it
        /// exists to replace, and the field name belongs after the consequence rather
        /// than instead of it.
        /// </summary>
        public string Consequence;
    }

    /// <summary>
    /// Where the managers' wiring fields point, and what goes wrong without them.
    ///
    /// <b>By GUID, not by name.</b> Every asset in this package had its GUID regenerated
    /// at extraction and shares none with <c>com.acchosen.vr-stage-lighting</c>, which
    /// ships CustomRenderTextures with identical names. Both packages installed together
    /// is a supported configuration, so a name search would not merely be fragile here —
    /// it would resolve to the other package's assets, and a manager reading the legacy
    /// decode chain reports green on every diagnostic while every channel decodes zero.
    /// That is the exact fault this milestone exists to remove.
    /// </summary>
    static class VRSLWiring
    {
        /// <summary>Where this package's assets live in the AssetDatabase, including when
        /// it is referenced by a local <c>file:</c> path — Unity surfaces those under
        /// <c>Packages/</c> like any other. One spelling, so a row checking that
        /// resolution stayed inside the package and the resolver itself cannot disagree
        /// about what "inside" means.</summary>
        public const string PackageRoot = "Packages/town.mr.vrsl-urp/";

        /// <summary>
        /// The DMX manager's ten. Every one has a single correct value, none of them can
        /// be worked out by an author, and each fails without an error.
        /// </summary>
        public static readonly VRSLWiringField[] Dmx =
        {
            new() { Field = "dmxMainTexture",
                    Guid  = "555ce9e464c9a54c9d331e0030c258ef",
                    Consequence = "every channel reads zero, so nothing lights at all" },
            new() { Field = "dmxMovementTexture",
                    Guid  = "63c403376634fef0560b44d7f19aedaa",
                    Consequence = "moving heads stay where they are" },
            new() { Field = "dmxStrobeTexture",
                    Guid  = "e3fa773f91559868d4104ab3ce54bf79",
                    Consequence = "strobe does nothing" },
            // The one the tooltip calls optional and the shipped prefabs leave empty.
            // Optional only in a scene that never strobes: the StrobeOutput CRT's decode
            // shader samples this, so where strobe is used a missing one takes the whole
            // rig dark rather than merely stopping the flash.
            new() { Field = "dmxStrobeTimerTexture",
                    Guid  = "e6641cd38ad1a21be3425ab43ecc2506",
                    Consequence = "every fixture goes dark wherever strobe is used" },
            new() { Field = "dmxSpinTimerTexture",
                    Guid  = "5bee9c07b3305e88be89de0d01fc2f07",
                    Consequence = "gobos stop spinning" },
            new() { Field = "computeShader",
                    Guid  = "213d3149ed0cbc73835aee17d5180332",
                    Consequence = "nothing lights at all" },
            new() { Field = "lightCullShader",
                    Guid  = "5f41dc4adb8e42f8937cb0064c39ea17",
                    Consequence = "the picture is right and every pixel pays for every "
                                + "fixture on screen" },
            new() { Field = "lightingShader",
                    Guid  = "d8c757279dc0dc7ff544149ea9f3e897",
                    Consequence = "no light lands on surfaces" },
            new() { Field = "surfacePropertiesShader",
                    Guid  = "5e926720f16a403487984bfa2578e36f",
                    Consequence = "every surface lights as flat grey, whatever colour it is" },
            new() { Field = "volumetricShader",
                    Guid  = "ee4b25e5a146ba8b36ed731b36047774",
                    Consequence = "no beams in the air" },
        };

        /// <summary>
        /// The AudioLink manager's five. Four are the same assets the DMX manager uses —
        /// the cull, the lighting, the prepass and the volumetric are shared — and only
        /// the compute differs.
        ///
        /// <c>samplingTexture</c> is deliberately not here. It accepts any texture, falls
        /// back to AudioLink's own atlas when empty, and is a palette an author chooses:
        /// content rather than wiring, like <c>goboTextures</c>.
        /// </summary>
        public static readonly VRSLWiringField[] AudioLink =
        {
            new() { Field = "computeShader",
                    Guid  = "b62eba04b10a6cc19700ff31693a9066",
                    Consequence = "nothing lights at all" },
            new() { Field = "lightCullShader",
                    Guid  = "5f41dc4adb8e42f8937cb0064c39ea17",
                    Consequence = "the picture is right and every pixel pays for every "
                                + "fixture on screen" },
            new() { Field = "lightingShader",
                    Guid  = "d8c757279dc0dc7ff544149ea9f3e897",
                    Consequence = "no light lands on surfaces" },
            new() { Field = "surfacePropertiesShader",
                    Guid  = "5e926720f16a403487984bfa2578e36f",
                    Consequence = "every surface lights as flat grey, whatever colour it is" },
            new() { Field = "volumetricShader",
                    Guid  = "ee4b25e5a146ba8b36ed731b36047774",
                    Consequence = "no beams in the air" },
        };

        /// <summary>Every field either manager has, deduplicated by GUID — which is what
        /// a row checking the table against the project wants to walk.</summary>
        public static IEnumerable<VRSLWiringField> All
        {
            get
            {
                var seen = new HashSet<string>();
                foreach (var f in Dmx)       if (seen.Add(f.Guid)) yield return f;
                foreach (var f in AudioLink) if (seen.Add(f.Guid)) yield return f;
            }
        }

        /// <summary>The asset a field points at, or null when the GUID resolves to
        /// nothing in this project.</summary>
        public static Object Load(VRSLWiringField field)
        {
            if (field == null || string.IsNullOrEmpty(field.Guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(field.Guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Object>(path);
        }

        /// <summary>The asset's path, for a message that has to name what it looked
        /// for. Empty when the GUID resolves to nothing.</summary>
        public static string PathOf(VRSLWiringField field) =>
            field == null ? "" : AssetDatabase.GUIDToAssetPath(field.Guid);

        /// <summary>The table for a manager, or null for anything else.</summary>
        public static VRSLWiringField[] FieldsFor(Object manager) => manager switch
        {
            VRSL_URPLightManager          => Dmx,
            VRSL_AudioLinkURPLightManager => AudioLink,
            _                             => null,
        };

        /// <summary>
        /// Fill every empty wiring field on a manager.
        ///
        /// <b>Empty ones only.</b> R-M7-3: a value an author set is theirs, however
        /// unusual it looks, and a resolver that enforced opinions would be a worse
        /// problem than the one it fixes. This fills gaps and nothing else.
        /// </summary>
        /// <returns>What it changed, and what it could not.</returns>
        public static VRSLWiringResult Resolve(Object manager)
        {
            var result = new VRSLWiringResult();
            var fields = FieldsFor(manager);
            if (fields == null) return result;

            // Through SerializedObject rather than by assigning the field: it registers
            // the undo, marks the object dirty, and writes a prefab override the way the
            // inspector would. Setting the field directly does none of the three, and the
            // change would be lost on the next reload without anything saying so.
            var so = new SerializedObject(manager);
            foreach (var field in fields)
            {
                var prop = so.FindProperty(field.Field);
                // Pinned by a row, so this is a table that has drifted rather than a
                // condition to expect. Skipped rather than thrown: one stale entry should
                // not stop the other nine being filled.
                if (prop == null) { result.Unresolved.Add(field); continue; }
                if (prop.objectReferenceValue != null) continue;

                var asset = Load(field);
                if (asset == null) { result.Unresolved.Add(field); continue; }

                prop.objectReferenceValue = asset;
                result.Filled.Add(field);
            }
            so.ApplyModifiedProperties();
            return result;
        }

        /// <summary>Entity ids, not references: a hold on a manager that is later
        /// destroyed must not keep the object alive, and an id can be compared without
        /// touching a dead one.</summary>
        static readonly HashSet<EntityId> s_suppressed = new();

        /// <summary>Whether automatic resolution is currently held off for this
        /// manager.</summary>
        public static bool AutoResolveSuppressed(Object manager) =>
            manager != null && s_suppressed.Contains(manager.GetEntityId());

        /// <summary>
        /// Hold automatic resolution off for one manager, for the life of the scope.
        ///
        /// It has to be holdable, and the reason is concrete. M0's quality preset turns
        /// volumetrics off by emptying <c>volumetricShader</c> and bouncing the manager —
        /// that emptied field is the mechanism, not a fault — and resolution fills empty
        /// wiring on enable. Without a hold the two fight: the shader comes straight
        /// back, the pass runs at full cost, and the counters still report it gone
        /// because they read the level rather than the field.
        ///
        /// <b>Per manager rather than process-wide, and that distinction is load
        /// bearing.</b> A global hold was tried first and a caller that legitimately
        /// never released it — the benchmark rows open a session on a manager they are
        /// about to destroy, so restoring fields on it is pointless — left automatic
        /// resolution dead for every later row in the process. It failed silently, which
        /// is the exact class of fault this milestone exists to remove. Scoped to one
        /// manager, a leak dies with that manager.
        /// </summary>
        public static System.IDisposable SuppressAutoResolve(Object manager) =>
            new Suppression(manager);

        sealed class Suppression : System.IDisposable
        {
            readonly EntityId _id;
            bool _released;

            public Suppression(Object manager)
            {
                _id = manager != null ? manager.GetEntityId() : default;
                if (_id != default) s_suppressed.Add(_id);
            }

            public void Dispose()
            {
                if (_released || _id == default) return;
                _released = true;
                s_suppressed.Remove(_id);
            }
        }

        /// <summary>
        /// Resolution as a manager's enable calls it.
        ///
        /// Says what it filled, rather than fixing the scene in silence. The risk this
        /// milestone carries is that a manager which quietly repairs itself is one whose
        /// author never learns their scene was wrong, and one line in the Console when
        /// something was actually empty is the cheapest answer to it. Nothing is logged
        /// on the ordinary case where there was nothing to do.
        /// </summary>
        public static void ResolveOnEnable(Object manager)
        {
            if (AutoResolveSuppressed(manager)) return;

            var result = Resolve(manager);
            if (!result.ChangedAnything) return;

            Debug.Log($"[VRSL] Set up {result.Filled.Count} empty setting(s) on "
                    + $"{manager.name}. Without them: "
                    + string.Join("; ", result.Filled.ConvertAll(f => f.Consequence)) + ".",
                      manager);
        }

        /// <summary>
        /// What is empty on a manager right now, without changing anything.
        ///
        /// Separate from <see cref="Resolve"/> because the inspector draws every repaint
        /// and resolution must not run per repaint — an AssetDatabase search per frame in
        /// a large project is its own kind of unusable.
        /// </summary>
        public static List<VRSLWiringField> Empty(Object manager)
        {
            var empty  = new List<VRSLWiringField>();
            var fields = FieldsFor(manager);
            if (fields == null) return empty;

            var so = new SerializedObject(manager);
            foreach (var field in fields)
            {
                var prop = so.FindProperty(field.Field);
                if (prop == null || prop.objectReferenceValue == null) empty.Add(field);
            }
            return empty;
        }
    }

    /// <summary>What a resolve changed, and what it could not.</summary>
    class VRSLWiringResult
    {
        public readonly List<VRSLWiringField> Filled     = new();
        public readonly List<VRSLWiringField> Unresolved = new();

        public bool ChangedAnything => Filled.Count > 0;

        /// <summary>
        /// What to tell the author, in the register R-M7-5 asks for: what they would
        /// have seen, then which field it was.
        ///
        /// Reported rather than done in silence, because a manager that quietly fixes
        /// itself is one whose author never learns their scene was wrong.
        /// </summary>
        public string Describe()
        {
            var text = new System.Text.StringBuilder();
            if (Filled.Count > 0)
            {
                text.AppendLine($"Filled in {Filled.Count} setting(s) that were empty:");
                foreach (var f in Filled)
                    text.AppendLine($"  • {Capitalise(f.Consequence)} ({f.Field}).");
            }
            if (Unresolved.Count > 0)
            {
                if (Filled.Count > 0) text.AppendLine();
                text.AppendLine($"{Unresolved.Count} could not be filled in, and this is what "
                              + "you will see:");
                foreach (var f in Unresolved)
                    text.AppendLine($"  • {Capitalise(f.Consequence)} ({f.Field}). Looked "
                                  + $"for {PathOrMissing(f)}.");
            }
            if (text.Length == 0) text.Append("Everything was already set up.");
            return text.ToString().TrimEnd();
        }

        static string PathOrMissing(VRSLWiringField field)
        {
            string path = VRSLWiring.PathOf(field);
            return string.IsNullOrEmpty(path)
                 ? "an asset that is not in this project any more"
                 : path;
        }

        static string Capitalise(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
#endif
