// The migration utility only makes sense when the upstream
// com.acchosen.vr-stage-lighting package is installed alongside this package.
// VRSL_LEGACY_PRESENT is added by Towneh.VRSL.URP.Editor.asmdef versionDefines
// when that package is present, so the entire file (and its menu item) only
// exists in projects that actually have legacy fixtures to migrate.
#if VRSL_LEGACY_PRESENT
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRSL.URP;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Editor utility that walks the active scene, finds every prefab instance of an
    /// upstream com.acchosen.vr-stage-lighting fixture, and inserts the matching
    /// VRSL-URP fixture as a sibling immediately below it, its body where the original's
    /// is. Source instances are left in place for the author to delete once happy with
    /// the result.
    ///
    /// Match is by source-prefab GUID (PrefabUtility.GetCorrespondingObjectFromOriginalSource
    /// → AssetPath → AssetPathToGUID), so prefab renames in either package don't break it.
    /// Component fields are copied via reflection so this assembly never has to link the
    /// upstream package.
    ///
    /// Idempotent: re-running skips any source that already has a matching URP-prefab
    /// instance beside it, standing where the tool places one or on the source's own root.
    /// </summary>
    public static class VRSL_LegacyToUrpMigration
    {
        const string MENU_PATH = "VRSL/URP/Migrate Scene Fixtures (Add URP Siblings)";
        const string MENU_PATH_INPLACE = "VRSL/URP/Convert Custom Fixtures In-Place (Component + Material)";

        static readonly Dictionary<string, VRSLMigrationPair> PAIRS_BY_UPSTREAM_GUID =
            VRSLMigrationTable.Pairs.ToDictionary(p => p.UpstreamGuid);

        // Upstream component type names we look up by string (no compile-time reference to upstream).
        static readonly VRSLUpstreamTypes UPSTREAM = VRSLUpstreamTypes.Upstream;

        // Upstream component FullName -> URP twin FullName, for the in-place conversion path. Both
        // twins keep the upstream field layout, so values copy across by name with no translation.
        static readonly (string upstreamTypeName, string urpTypeName)[] IN_PLACE_TYPES = new[]
        {
            (UPSTREAM.DmxStatic,       VRSLMigrationCopy.URP_DMX_STATIC),
            (UPSTREAM.AudioLinkStatic, VRSLMigrationCopy.URP_AL_STATIC),
            (UPSTREAM.AudioLinkLaser,  VRSLMigrationCopy.URP_AL_LASER),
        };

        const string LEFT_FOR_AUDIOLINK =
            "AudioLink fixtures left as they are, because AudioLink isn't installed. "
            + "Install AudioLink, then run this again:";

        static Type UrpType(string fullName) => VRSLMigrationCopy.UrpType(fullName);

        // Position-equality tolerance for idempotency check (1mm).
        const float POSITION_EPSILON = 0.001f;

        [MenuItem(MENU_PATH, false, 120)]
        static void Run()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                EditorUtility.DisplayDialog("VRSL Migration", "No active loaded scene.", "OK");
                return;
            }

            VRSLMigrationCopy.BeginRun();

            // Snapshot candidates before mutating — otherwise we'd walk into the URP siblings
            // we just inserted and try to migrate them too.
            var candidates = new List<(GameObject go, VRSLMigrationPair pair)>();
            var unported = new Dictionary<string, int>();
            var leftForAudioLink = new Dictionary<string, int>();
            bool audioLinkInstalled = UrpType(VRSLMigrationCopy.URP_AL_REALTIME) != null;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive: true))
                {
                    if (!TryGetSourceGuid(t.gameObject, out string sourceGuid)) continue;
                    if (PAIRS_BY_UPSTREAM_GUID.TryGetValue(sourceGuid, out var pair))
                    {
                        if (pair.Kind == VRSLMigrationKind.AudioLink && !audioLinkInstalled)
                        {
                            leftForAudioLink.TryGetValue(pair.FriendlyName, out int left);
                            leftForAudioLink[pair.FriendlyName] = left + 1;
                        }
                        else candidates.Add((t.gameObject, pair));
                    }
                    else if (VRSLMigrationTable.Unported.TryGetValue(sourceGuid, out var name))
                    {
                        unported.TryGetValue(name, out int n);
                        unported[name] = n + 1;
                    }
                }
            }

            int created = 0, skipped = 0, errors = 0;
            var perKind = new Dictionary<string, int>();
            var siblings = new List<(GameObject original, GameObject sibling)>();

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("VRSL: Add URP Siblings");
            int undoGroup = Undo.GetCurrentGroup();

            foreach (var (go, pair) in candidates)
            {
                var existing = FindUrpSibling(go.transform, pair);
                if (existing != null)
                {
                    skipped++;
                    siblings.Add((go, existing.gameObject));
                    continue;
                }
                try
                {
                    var newGo = AddUrpSibling(go.transform, pair);
                    if (newGo != null)
                    {
                        created++;
                        siblings.Add((go, newGo));
                        perKind.TryGetValue(pair.FriendlyName, out int n);
                        perKind[pair.FriendlyName] = n + 1;
                    }
                    else
                    {
                        errors++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[VRSL Migration] Failed on '{go.name}': {ex}");
                    errors++;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);

            string summary = $"Created: {created}\nSkipped (already migrated): {skipped}\nErrors: {errors}";
            summary += NotCarriedParagraph();
            summary += DeletingParagraph(scene, siblings);
            if (perKind.Count > 0)
                summary += "\n\nBy fixture:\n" + string.Join("\n",
                    perKind.OrderBy(k => k.Key).Select(k => $"  {k.Key}: {k.Value}"));
            if (unported.Count > 0)
                summary += "\n\nLeft as they are, since no URP prefab exists for them:\n" + string.Join("\n",
                    unported.OrderBy(k => k.Key).Select(k => $"  {k.Key}: {k.Value}"));
            if (leftForAudioLink.Count > 0)
                summary += "\n\n" + LEFT_FOR_AUDIOLINK + "\n" + string.Join("\n",
                    leftForAudioLink.OrderBy(k => k.Key).Select(k => $"  {k.Key}: {k.Value}"));
            summary += LightStencilParagraph(scene);

            Debug.Log("[VRSL Migration] " + summary.Replace("\n", "  "));
            EditorUtility.DisplayDialog("VRSL Migration", summary, "OK");
        }

        // ── In-place conversion (custom fixtures) ───────────────────────────────────

        /// <summary>
        /// Converts fixtures that carry an upstream VRSL component directly, such as a custom mesh
        /// (an imported lighting rig) with upstream's VRStageLighting_DMX_Static added onto it,
        /// rather than instances of a standard VRSL fixture prefab. The prefab-swap migration above
        /// can't help those: there's no source-prefab GUID in its table and no URP prefab carrying
        /// the custom geometry. This path keeps the geometry and retargets the driver: it adds the
        /// URP twin component (same field layout), copies every field across, swaps the fixture's
        /// VRSL materials to the matching URP shader, moves references from the upstream component
        /// to the URP one where they can follow, and removes the upstream component. Matching is by
        /// component, so nesting, unpacking, and prefab GUIDs are all irrelevant.
        ///
        /// Idempotent: a fixture that already has the URP twin is skipped, and material swaps key
        /// on the shader namespace (VRSL/… → VRSL-URP/…) so a second run is a no-op.
        /// </summary>
        [MenuItem(MENU_PATH_INPLACE, false, 121)]
        static void RunInPlace()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                EditorUtility.DisplayDialog("VRSL In-Place Conversion", "No active loaded scene.", "OK");
                return;
            }

            VRSLMigrationCopy.BeginRun();

            // Snapshot first: we add and remove components as we go, so walking live would be unsafe.
            var candidates = new List<(GameObject go, Component upstream, Type urpType)>();
            int leftForAudioLink = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var c in root.GetComponentsInChildren<Component>(includeInactive: true))
                {
                    if (c == null) continue;
                    string typeName = c.GetType().FullName;
                    foreach (var map in IN_PLACE_TYPES)
                    {
                        if (typeName != map.upstreamTypeName) continue;
                        var urpType = UrpType(map.urpTypeName);
                        if (urpType != null) candidates.Add((c.gameObject, c, urpType));
                        else leftForAudioLink++;
                    }
                }
            }

            int converted = 0, skipped = 0, errors = 0;
            var seenMaterials = new HashSet<Material>();      // every material inspected once
            var changedMaterials = new HashSet<Material>();   // subset actually reshaded -> SaveAssets

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("VRSL: Convert Custom Fixtures In-Place");
            int undoGroup = Undo.GetCurrentGroup();

            // Every URP component first, so a fixture that references another converted one has its
            // copy in place before references move; then the references; then the originals go.
            var replaced = new List<(Component upstream, Component urp)>();
            foreach (var (go, upstream, urpType) in candidates)
            {
                if (go.GetComponent(urpType) != null)
                {
                    skipped++;
                    continue;
                }
                try
                {
                    var urp = Undo.AddComponent(go, urpType);
                    VRSLMigrationCopy.CopyAllFieldsByName(upstream, urp);
                    SwapFixtureMaterials(go, seenMaterials, changedMaterials);
                    replaced.Add((upstream, urp));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[VRSL In-Place] Failed on '{go.name}': {ex}");
                    errors++;
                }
            }

            int referencesMoved = 0, referencesLeft = 0;
            var index = new VRSLMigrationCopy.ReferenceIndex(scene);
            foreach (var (upstream, urp) in replaced)
            {
                var left = VRSLMigrationCopy.Repoint(upstream, urp, index, out int moved);
                referencesMoved += moved;
                referencesLeft += left.Count;
                foreach (var (holder, path) in left)
                    Debug.LogWarning($"[VRSL In-Place] {holder.GetType().Name} on '{holder.name}' "
                                   + $"({VRSLMigrationCopy.FieldName(path)}) pointed at the original VRSL component on "
                                   + $"'{upstream.name}', which the conversion removes. It can't hold the URP one, so "
                                   + "it's left empty. Point it at the URP fixture by hand.", holder);
            }

            foreach (var (upstream, _) in replaced)
            {
                Undo.DestroyObjectImmediate(upstream);
                converted++;
            }

            if (changedMaterials.Count > 0)
                AssetDatabase.SaveAssets();

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);

            string summary =
                $"Converted: {converted}\nSkipped (already URP): {skipped}\nErrors: {errors}\n" +
                $"Materials moved to URP shaders: {changedMaterials.Count}";
            if (referencesMoved + referencesLeft > 0)
                summary += $"\n\nReferences to the original components moved to the URP ones: {referencesMoved}"
                         + (referencesLeft > 0
                            ? $"\nLeft empty, because they can't hold the URP component: {referencesLeft}. "
                              + "Each is listed in the Console; clicking a line selects it."
                            : "");
            if (leftForAudioLink > 0)
                summary += $"\n\n{LEFT_FOR_AUDIOLINK}\n  AudioLink fixtures: {leftForAudioLink}";
            summary += LightStencilParagraph(scene);
            Debug.Log("[VRSL In-Place] " + summary.Replace("\n", "  "));
            EditorUtility.DisplayDialog("VRSL In-Place Conversion", summary, "OK");
        }

        // ── Detection ─────────────────────────────────────────────────────────────

        // Returns the source-prefab GUID for `go` only when `go` is the outermost prefab
        // root of a scene instance — children inside a fixture should not each spawn a
        // URP sibling, and we identify by root-prefab GUID anyway.
        static bool TryGetSourceGuid(GameObject go, out string guid)
        {
            guid = null;
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root != go) return false;

            var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
            if (src == null) return false;

            string path = AssetDatabase.GetAssetPath(src);
            if (string.IsNullOrEmpty(path)) return false;

            guid = AssetDatabase.AssetPathToGUID(path);
            return !string.IsNullOrEmpty(guid);
        }

        // Upstream's light stencils in the scene, as a paragraph for the summary (empty when there
        // are none). An instance of a mask prefab counts once and its children aren't counted
        // again; any other renderer counts once per stencil material it uses, or per material
        // of the world's own on a stencil shader. Each object found gets a Console line that
        // selects it when clicked. Nothing is changed: deleting them is the author's call.
        static string LightStencilParagraph(Scene scene)
        {
            var found = new Dictionary<string, int>();
            void Count(string guid, GameObject go)
            {
                string name = VRSLMigrationTable.LightStencils[guid];
                found.TryGetValue(name, out int n);
                found[name] = n + 1;
                Debug.Log($"[VRSL Migration] '{go.name}' is a light stencil from the original VRSL ({name}). "
                        + "It no longer keeps light off anything.", go);
            }

            var masks = new List<Transform>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive: true))
                {
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject) || masks.Any(m => t.IsChildOf(m))) continue;
                    string guid = LightStencilGuid(PrefabUtility.GetCorrespondingObjectFromOriginalSource(t.gameObject));
                    if (guid == null) continue;
                    masks.Add(t);
                    Count(guid, t.gameObject);
                }

            foreach (var root in scene.GetRootGameObjects())
                foreach (var r in root.GetComponentsInChildren<Renderer>(includeInactive: true))
                {
                    if (masks.Any(m => r.transform.IsChildOf(m))) continue;
                    var counted = new HashSet<Material>();
                    foreach (var mat in r.sharedMaterials)
                    {
                        if (mat == null) continue;
                        string guid = LightStencilGuid(mat) ?? LightStencilGuid(mat.shader);
                        if (guid != null && counted.Add(mat)) Count(guid, r.gameObject);
                    }
                }

            string paragraph = VRSLMigrationTable.LightStencilSummary(found);
            return paragraph.Length > 0 ? "\n\n" + paragraph : "";
        }

        // Settings authors changed on the originals that the URP fixtures have no place for, by
        // setting, as a paragraph for the summary (empty when there are none).
        static string NotCarriedParagraph()
        {
            var notCarried = VRSLMigrationCopy.NotCarried;
            if (notCarried.Count == 0) return "";
            return "\n\nSettings you changed on the originals that the URP fixtures have no place for, so they didn't "
                 + "come across (each fixture is listed in the Console):\n"
                 + string.Join("\n", notCarried.OrderBy(k => k.Key)
                       .Select(k => $"  {ObjectNames.NicifyVariableName(k.Key)}: {k.Value}"));
        }

        // What deleting the originals would take with it, each listed in the Console with where it
        // belongs on the URP fixture, as a paragraph for the summary (empty when there's nothing).
        static string DeletingParagraph(Scene scene, List<(GameObject original, GameObject sibling)> siblings)
        {
            var index = new VRSLMigrationCopy.ReferenceIndex(scene);
            int references = 0, added = 0;
            foreach (var (original, sibling) in siblings)
            {
                if (original == null || sibling == null) continue;
                var losses = VRSLMigrationCopy.WhatDeletingTakes(original, sibling,
                    VRSLMigrationCopy.OriginalFixture(original, UPSTREAM), VRSLMigrationCopy.SiblingFixture(sibling), index);
                foreach (var loss in losses)
                {
                    if (loss.Path != null)
                    {
                        references++;
                        var holder = (Component)loss.Thing;
                        Debug.Log($"[VRSL Migration] {holder.GetType().Name} on '{holder.name}' "
                                + $"({VRSLMigrationCopy.FieldName(loss.Path)}) points at {Describe(loss.Target)} on the "
                                + $"original fixture '{original.name}', so deleting the original breaks it. "
                                + (loss.Counterpart != null
                                    ? $"Point it at {Describe(loss.Counterpart)} on '{sibling.name}' first."
                                    : $"Nothing on '{sibling.name}' matches; decide what it should point at."), holder);
                    }
                    else
                    {
                        added++;
                        Debug.Log($"[VRSL Migration] {Describe(loss.Thing)} was added to the original fixture "
                                + $"'{original.name}' and goes with it if you delete it. "
                                + (loss.Counterpart != null
                                    ? $"Move it to '{loss.Counterpart.name}' on '{sibling.name}' first."
                                    : $"Move it to '{sibling.name}' first."), loss.Thing);
                    }
                }
            }
            if (references + added == 0) return "";
            return "\n\nBefore deleting the original fixtures:\n"
                 + $"  References to them from elsewhere in the scene: {references}\n"
                 + $"  Objects and components added to them: {added}\n"
                 + "Each is listed in the Console with where it belongs on the URP fixture; clicking a line selects it.";
        }

        static string Describe(UnityEngine.Object o) => o switch
        {
            GameObject g => $"'{g.name}'",
            Component c  => $"its {c.GetType().Name} on '{c.name}'",
            _            => $"'{o.name}'",
        };

        // The GUID of the asset `asset` was loaded from, when it's one of upstream's light stencils.
        static string LightStencilGuid(UnityEngine.Object asset)
        {
            if (asset == null) return null;
            string path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path)) return null;
            string guid = AssetDatabase.AssetPathToGUID(path);
            return VRSLMigrationTable.LightStencils.ContainsKey(guid) ? guid : null;
        }

        // Idempotent check: any sibling of `source` (under the same parent, or any scene root
        // when `source` is itself a root) that is a URP-prefab instance standing where the tool
        // places one, or on the source's own root, counts as already migrated. Matching by
        // position finds the sibling even after the hierarchy has been reordered by hand.
        static Transform FindUrpSibling(Transform source, VRSLMigrationPair pair)
        {
            var parent = source.parent;
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    var sibling = parent.GetChild(i);
                    if (sibling == source) continue;
                    if (IsMigratedSibling(sibling, source, pair)) return sibling;
                }
            }
            else
            {
                foreach (var rootGo in source.gameObject.scene.GetRootGameObjects())
                {
                    var sibling = rootGo.transform;
                    if (sibling == source) continue;
                    if (IsMigratedSibling(sibling, source, pair)) return sibling;
                }
            }
            return null;
        }

        static bool IsMigratedSibling(Transform sibling, Transform source, VRSLMigrationPair pair)
        {
            if (!TryGetSourceGuid(sibling.gameObject, out string siblingGuid)) return false;
            if (siblingGuid != pair.UrpGuid) return false;
            return VRSLMigrationCopy.IsPlacedFor(sibling, source, pair, POSITION_EPSILON);
        }

        // ── Insertion ─────────────────────────────────────────────────────────────

        static GameObject AddUrpSibling(Transform source, VRSLMigrationPair pair)
        {
            string urpPath = AssetDatabase.GUIDToAssetPath(pair.UrpGuid);
            if (string.IsNullOrEmpty(urpPath))
            {
                Debug.LogWarning($"[VRSL Migration] URP prefab GUID {pair.UrpGuid} ({pair.FriendlyName}) is not in the AssetDatabase. Is the VRSL-URP package imported?");
                return null;
            }
            var urpAsset = AssetDatabase.LoadAssetAtPath<GameObject>(urpPath);
            if (urpAsset == null) return null;

            var sourceAsset = PrefabUtility.GetCorrespondingObjectFromOriginalSource(source.gameObject);
            return VRSLMigrationCopy.PlaceSibling(source, sourceAsset, urpAsset, pair, UPSTREAM);
        }

        // ── Materials ─────────────────────────────────────────────────────────────

        // Swap any VRSL/ material on the fixture (and its children) to the matching VRSL-URP/
        // shader. The two shader sets share names apart from the namespace prefix, so the URP target
        // is resolved by name rather than a GUID table. Only writable project materials are touched;
        // package materials are left alone. Materials are deduped across the whole run.
        static void SwapFixtureMaterials(GameObject fixture, HashSet<Material> seen, HashSet<Material> changed)
        {
            const string SRC_PREFIX = "VRSL/";
            const string DST_PREFIX = "VRSL-URP/";
            foreach (var r in fixture.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null || !seen.Add(mat)) continue;
                    var shader = mat.shader;
                    if (shader == null || !shader.name.StartsWith(SRC_PREFIX)) continue;

                    string path = AssetDatabase.GetAssetPath(mat);
                    if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/"))
                    {
                        Debug.LogWarning($"[VRSL In-Place] Skipped non-writable material '{mat.name}' ({path}).");
                        continue;
                    }

                    string urpName = DST_PREFIX + shader.name.Substring(SRC_PREFIX.Length);
                    var urpShader = Shader.Find(urpName);
                    if (urpShader == null)
                    {
                        Debug.LogWarning($"[VRSL In-Place] No URP shader '{urpName}' for material '{mat.name}'; left on legacy shader.");
                        continue;
                    }
                    if (urpShader == shader) continue;

                    Undo.RecordObject(mat, "VRSL: Swap Material Shader");
                    mat.shader = urpShader;
                    EditorUtility.SetDirty(mat);
                    changed.Add(mat);
                }
            }
        }
    }
}
#endif
