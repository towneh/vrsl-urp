// Editor-only in substance: resolution reads the AssetDatabase, which a player has no
// equivalent of, and the resolved reference is serialised so a built player
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
        /// register to write in; "surfacePropertiesShader is null" is the thing it
        /// exists to replace, and the field name belongs after the consequence rather
        /// than instead of it.
        /// </summary>
        public string Consequence;

        /// <summary>
        /// Whether empty is a choice rather than a fault.
        ///
        /// Three of these may be left empty: the tile-cull and surface-properties tooltips say
        /// so (empty disables tile culling, or skips the surface prepass), and quality Off
        /// works by emptying the volumetric shader. Filling them in automatically would take
        /// a documented capability away and make an author's deliberate clear impossible to
        /// express. They are offered by a repair and never forced.
        /// </summary>
        public bool Optional;
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
    /// That is the fault this table exists to prevent.
    /// </summary>
    static class VRSLWiring
    {
        /// <summary>Where this package's assets live in the AssetDatabase, including when
        /// it is referenced by a local <c>file:</c> path — Unity surfaces those under
        /// <c>Packages/</c> like any other. One spelling, so a row checking that
        /// resolution stayed inside the package and the resolver itself cannot disagree
        /// about what "inside" means.</summary>
        public const string PackageRoot = "Packages/town.mr.vrsl-urp/";

        /// <summary>The package's own <see cref="VRSLGridTextures"/>, holding its decode
        /// textures for every grid layout.</summary>
        public const string GridTexturesGuid = "12d2839adb19481aa06544b434fb6f0e";

        /// <summary>
        /// The DMX manager's six. Every one has a single correct value, none of them can
        /// be worked out by an author, and each fails without an error.
        /// </summary>
        public static readonly VRSLWiringField[] Dmx =
        {
            new() { Field = "gridTextures",
                    Guid  = GridTexturesGuid,
                    Consequence = "nothing lights from the video at all" },
            new() { Field = "computeShader",
                    Guid  = "213d3149ed0cbc73835aee17d5180332",
                    Consequence = "nothing lights at all" },
            new() { Optional = true, Field = "lightCullShader",
                    Guid  = "5f41dc4adb8e42f8937cb0064c39ea17",
                    Consequence = "the picture is right and every pixel pays for every "
                                + "fixture on screen" },
            new() { Field = "lightingShader",
                    Guid  = "d8c757279dc0dc7ff544149ea9f3e897",
                    Consequence = "no light lands on surfaces" },
            new() { Optional = true, Field = "surfacePropertiesShader",
                    Guid  = "5e926720f16a403487984bfa2578e36f",
                    Consequence = "every surface lights as flat grey, whatever colour it is" },
            new() { Optional = true, Field = "volumetricShader",
                    Guid  = "ee4b25e5a146ba8b36ed731b36047774",
                    Consequence = "no beams in the air" },
        };

        /// <summary>The tables of managers from optional integrations, which register
        /// their own at editor load so this assembly never names them.</summary>
        static readonly Dictionary<System.Type, VRSLWiringField[]> s_Registered = new();

        /// <summary>Add a manager type's table. Registering it again replaces it.</summary>
        public static void Register(System.Type manager, VRSLWiringField[] fields)
        {
            if (manager != null && fields != null) s_Registered[manager] = fields;
        }

        /// <summary>Every field any manager has, deduplicated by GUID — which is what
        /// a row checking the table against the project wants to walk.</summary>
        public static IEnumerable<VRSLWiringField> All
        {
            get
            {
                var seen = new HashSet<string>();
                foreach (var f in Dmx) if (seen.Add(f.Guid)) yield return f;
                foreach (var table in s_Registered.Values)
                    foreach (var f in table) if (seen.Add(f.Guid)) yield return f;
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
        public static VRSLWiringField[] FieldsFor(Object manager)
        {
            if (manager == null) return null;
            if (manager is VRSL_DMXURPLightManager) return Dmx;
            return s_Registered.TryGetValue(manager.GetType(), out var fields) ? fields : null;
        }

        /// <summary>
        /// Fill every empty wiring field on a manager.
        ///
        /// <b>Empty ones only.</b> A value an author set is theirs, however
        /// unusual it looks, and a resolver that enforced opinions would be a worse
        /// problem than the one it fixes. This fills gaps and nothing else.
        /// </summary>
        /// <returns>What it changed, and what it could not.</returns>
        public static VRSLWiringResult Resolve(Object manager, bool includeOptional = false)
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
                // not stop the rest being filled.
                if (prop == null) { result.Unresolved.Add(field); continue; }
                if (prop.objectReferenceValue != null) continue;
                // Left alone unless asked for. Empty means something on these, so filling
                // one automatically would undo a choice rather than fix a mistake.
                if (field.Optional && !includeOptional) continue;

                var asset = Load(field);
                if (asset == null) { result.Unresolved.Add(field); continue; }

                prop.objectReferenceValue = asset;
                result.Filled.Add(field);
            }
            so.ApplyModifiedProperties();
            return result;
        }

        /// <summary>
        /// How many holds each manager has, keyed by entity id.
        ///
        /// Entity ids rather than references, so a hold on a manager that is later
        /// destroyed neither keeps it alive nor has to touch a dead one. <b>Counted
        /// rather than a set</b>, because two scopes on one manager are possible and a
        /// set would let the first release the second's hold — quality Off would then
        /// come back on at the next bounce, which is the fault the hold exists to stop.
        /// </summary>
        static readonly Dictionary<EntityId, int> s_suppressed = new();

        /// <summary>Whether automatic resolution is currently held off for this
        /// manager.</summary>
        public static bool AutoResolveSuppressed(Object manager) =>
            manager != null && s_suppressed.ContainsKey(manager.GetEntityId());

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
        /// is the kind of fault self-wiring exists to prevent. Scoped to one
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
                if (_id == default) return;
                s_suppressed.TryGetValue(_id, out int held);
                s_suppressed[_id] = held + 1;
            }

            public void Dispose()
            {
                // Guarded because a scope disposed twice would drop somebody else's hold.
                if (_released || _id == default) return;
                _released = true;
                if (!s_suppressed.TryGetValue(_id, out int held)) return;
                if (held <= 1) s_suppressed.Remove(_id);
                else           s_suppressed[_id] = held - 1;
            }
        }

        /// <summary>
        /// Resolution as a manager's enable calls it.
        ///
        /// Says what it filled, rather than fixing the scene in silence. The risk this
        /// design carries is that a manager which silently repairs itself is one whose
        /// author never learns their scene was wrong, and one line in the Console when
        /// something was actually empty is the cheapest answer to it. Nothing is logged
        /// on the ordinary case where there was nothing to do.
        /// </summary>
        public static void ResolveOnEnable(Object manager)
        {
            if (AutoResolveSuppressed(manager)) return;

            // Before resolution, which would otherwise fill an old manager's empty Grid
            // Textures and leave it reading the Horizontal set whatever it was set up for.
            if (manager is VRSL_DMXURPLightManager dmx) VRSLGridUpgrade.Upgrade(dmx);
            // Before the manager builds its wheel, which it does from what this leaves.
            if (manager is IVRSLLightManager && manager is MonoBehaviour behaviour)
                VRSLGoboWheelUpgrade.Upgrade(behaviour);

            var result = Resolve(manager);
            if (!result.ChangedAnything && result.Unresolved.Count == 0) return;

            // A warning when something is still empty, because the author will see it go
            // wrong and nothing else is going to tell them. An unresolvable field used to
            // pass in silence here: nothing was filled, so nothing was said, and a manager
            // whose assets are missing entirely said the least of all.
            string message = $"[VRSL] Wiring on {manager.name}:"
                           + System.Environment.NewLine + result.Describe();
            if (result.Unresolved.Count > 0) Debug.LogWarning(message, manager);
            else                             Debug.Log(message, manager);
        }

        /// <summary>Managers with a deferred resolve already queued, so a burst of
        /// OnValidate calls — dragging any slider on the component sends one per
        /// change — schedules one pass rather than dozens.</summary>
        static readonly HashSet<EntityId> s_pending = new();

        /// <summary>
        /// Resolution as a manager's OnValidate calls it.
        ///
        /// Enable covers a scene loading and a component being switched on; this covers
        /// the author who empties a field in the inspector and would otherwise have to
        /// know to reload the scene.
        ///
        /// <b>Deferred, and not merely as a precaution.</b> OnValidate runs during
        /// import and deserialisation, where reaching into the AssetDatabase is not
        /// something to do — and it also runs while the inspector is mid-edit, where
        /// writing the object back underneath the control being dragged is its own kind
        /// of rude. The next editor tick is late enough for both.
        ///
        /// <b>Scene objects only.</b> A prefab asset gets OnValidate on import, and
        /// resolving there would rewrite assets — this package's own shipped prefabs
        /// among them — as a side effect of opening a project. An author who wants a
        /// prefab repaired can ask for it.
        /// </summary>
        public static void ResolveOnValidate(Object manager)
        {
            if (manager == null || EditorUtility.IsPersistent(manager)) return;

            var id = manager.GetEntityId();
            if (!s_pending.Add(id)) return;

            var target = manager;
            EditorApplication.delayCall += () => RunPending(id, target);
        }

        /// <summary>
        /// Do the work <see cref="ResolveOnValidate"/> queued.
        ///
        /// Separate from the scheduling so a row can drive it. <c>delayCall</c> does not
        /// fire inside a batch-mode play-mode test — measured, two rows sat waiting ten
        /// frames for a pass that never came — so a row that waited on it would be
        /// testing Unity's scheduler rather than any of this. What is worth testing is
        /// that validate queues rather than resolving under the inspector's feet, and
        /// that the queued pass fills what was empty. Both are here; the one line that
        /// hands this to the next editor tick is Unity's contract.
        /// </summary>
        internal static void RunPending(EntityId id, Object target)
        {
            s_pending.Remove(id);
            // Destroyed between the edit and the tick, which is ordinary: undoing the
            // add of a component queues this and then removes the object.
            if (target != null) ResolveOnEnable(target);
        }

        /// <summary>Drive whatever <see cref="ResolveOnValidate"/> queued for this
        /// manager, as the next editor tick would. For rows, and for nothing else.</summary>
        internal static void FlushPending(Object manager)
        {
            if (manager == null) return;
            RunPending(manager.GetEntityId(), manager);
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
        /// What to tell the author: what they would
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
                text.AppendLine(Filled.Count == 1
                    ? "Filled in one setting that was empty:"
                    : $"Filled in {Filled.Count} settings that were empty:");
                foreach (var f in Filled)
                    text.AppendLine($"  • {Capitalise(f.Consequence)} ({f.Field}).");
            }
            if (Unresolved.Count > 0)
            {
                if (Filled.Count > 0) text.AppendLine();
                text.AppendLine(Unresolved.Count == 1
                    ? "One setting could not be filled in, and this is what you will see:"
                    : $"{Unresolved.Count} settings could not be filled in, and this is what "
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
