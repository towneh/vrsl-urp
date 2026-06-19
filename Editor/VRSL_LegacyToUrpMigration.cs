// The migration utility only makes sense when the upstream
// com.acchosen.vr-stage-lighting package is installed alongside this package.
// VRSL_LEGACY_PRESENT is added by Towneh.VRSL.URP.Editor.asmdef versionDefines
// when that package is present, so the entire file (and its menu item) only
// exists in projects that actually have legacy fixtures to migrate.
#if VRSL_LEGACY_PRESENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// VRSL-URP fixture as a sibling immediately below it with the same world transform.
    /// Source instances are left in place — the URP siblings light the scene under URP
    /// while the upstream instances continue to render under the legacy pipeline. The
    /// author can delete the upstream instances once happy with the result.
    ///
    /// Match is by source-prefab GUID (PrefabUtility.GetCorrespondingObjectFromOriginalSource
    /// → AssetPath → AssetPathToGUID), so prefab renames in either package don't break it.
    /// Component fields are copied via reflection so this assembly never has to link the
    /// upstream package — the migration is a no-op if upstream isn't installed.
    ///
    /// Idempotent: re-running skips any source that already has a matching URP-prefab
    /// instance under the same parent at the same world position.
    /// </summary>
    public static class VRSL_LegacyToUrpMigration
    {
        const string MENU_PATH = "VRSL/URP/Migrate Scene Fixtures (Add URP Siblings)";
        const string MENU_PATH_INPLACE = "VRSL/URP/Convert Custom Fixtures In-Place (Component + Material)";

        // Upstream prefab GUID -> URP prefab GUID. Keep the friendly name in-line so the
        // table stays self-documenting. Kind drives which field-translation path runs.
        static readonly PrefabPair[] PREFAB_PAIRS = new[]
        {
            new PrefabPair("2aa50be2d32099842af2903a918a56f7", "17c6ee43b8af20aead4a4ccc0c57c3cb", "AudioLink Mover Spotlight",  FixtureKind.AudioLink),
            new PrefabPair("dd0fe316ce2ca824ead0901561087fd3", "9aa51ab9f0738edf41d2ab84a38c2784", "AudioLink Mover Washlight",  FixtureKind.AudioLink),
            new PrefabPair("269647a339f4d1c47951638c83aa839b", "2156195184d840df408a82f7ec31ecd9", "AudioLink Static Blinder",   FixtureKind.AudioLink),
            new PrefabPair("161d81f8a11b22d42ae4e81f522939d3", "3e6759ca4cb0a19bc3f20487b6c9f2e1", "AudioLink Static ParLight",  FixtureKind.AudioLink),

            new PrefabPair("f5be3cfe3f15bfb4e9477904c5af9daf", "d3e39e2f24901667db5024c000b520cc", "DMX H 13CH Mover Spotlight", FixtureKind.Dmx),
            new PrefabPair("b3e8ff051cc2d684aa255ceccce9b96f", "b3dde321f5bf01b7f34b56b4f022e9e3", "DMX H 13CH Mover Washlight", FixtureKind.Dmx),
            new PrefabPair("e9dde3e86ccb8ca4bb4ecbe35a6fa7b1", "0c48d81deda9b2b42e666ee404a350e5", "DMX H 13CH Static Blinder",  FixtureKind.Dmx),
            new PrefabPair("946b3c09cfa93244c90a4b0ac7764b44", "51451bcea7766727ec63db8b7aebe996", "DMX H 13CH Static ParLight", FixtureKind.Dmx),

            new PrefabPair("d9cab657bd2dff14ea5425c2c4c4679e", "12eb166a9c94e2b764534fdf24bef23b", "DMX L 13CH Mover Spotlight", FixtureKind.Dmx),
            new PrefabPair("41c8453c8957aec4292212174d351a36", "c9012c802489a64d6e6fcba1c829b084", "DMX L 13CH Mover Washlight", FixtureKind.Dmx),
            new PrefabPair("9310469001d6cdf4db2145f9fddd7933", "4bd409afbd3dcc666a94dfe0de01c748", "DMX L 13CH Static Blinder",  FixtureKind.Dmx),
            new PrefabPair("dd7cad5fc7f12624ea58efde5c3cd633", "89fe5f214c335d0bb96312a00515613b", "DMX L 13CH Static ParLight", FixtureKind.Dmx),

            new PrefabPair("9a6d4144bda0d3c4ba95593af446b653", "b4eb189266f0fc064ad34abf97e760db", "DMX V 13CH Mover Spotlight", FixtureKind.Dmx),
            new PrefabPair("88bee1a0ddf090d4bb0721b30240c949", "9433adb565b60265dd32d0b3d29495fe", "DMX V 13CH Mover Washlight", FixtureKind.Dmx),
            new PrefabPair("d7a8bacd5310e8e499962549ef931c57", "e9c7d3d37ed1b74a9921c9a2d3b05d95", "DMX V 13CH Static Blinder",  FixtureKind.Dmx),
            new PrefabPair("2ff8eb277ef9d7047b12d127b2eaeb36", "d762a8e3b9fd9395d8fc6d1a572d4429", "DMX V 13CH Static ParLight", FixtureKind.Dmx),

            new PrefabPair("5ae312c8e69488842994fd62a7609adc", "4bd6fe160f434eaa8ae0ad2b7e28d203", "DMX H 5CH Static Blinder",   FixtureKind.Dmx),
            new PrefabPair("6a94fea4f85300a44b9e29ba54430110", "e0009838b6fe1c3137b25f4e3658ae65", "DMX H 5CH Static ParLight",  FixtureKind.Dmx),
            new PrefabPair("94d6ff221dc5748458941750e422114f", "4138d57a3de4cdd841630c4e41cfed3d", "DMX V 5CH Static Blinder",   FixtureKind.Dmx),
            new PrefabPair("3b7bdfab2bd7abf4295be3356f6f3617", "6b5a89d8241bd316dc7ff37e0135e98c", "DMX V 5CH Static ParLight",  FixtureKind.Dmx),
        };

        static readonly Dictionary<string, PrefabPair> PAIRS_BY_UPSTREAM_GUID =
            PREFAB_PAIRS.ToDictionary(p => p.UpstreamGuid);

        // Upstream component type names we look up by string (no compile-time reference to upstream).
        const string UPSTREAM_AL_STATIC    = "VRSL.VRStageLighting_AudioLink_Static";
        const string UPSTREAM_AL_REALTIME  = "VRSL.VRStageLighting_AudioLink_RealtimeLight";
        const string UPSTREAM_DMX_STATIC   = "VRSL.VRStageLighting_DMX_Static";
        const string UPSTREAM_DMX_REALTIME = "VRSL.VRStageLighting_DMX_RealtimeLight";

        // Upstream component FullName -> URP twin Type, for the in-place conversion path. Both
        // twins keep the upstream field layout, so values copy across by name with no translation.
        static readonly (string upstreamTypeName, Type urpType)[] IN_PLACE_TYPES = new[]
        {
            (UPSTREAM_DMX_STATIC, typeof(VRStageLighting_DMX_Static)),
            (UPSTREAM_AL_STATIC,  typeof(VRStageLighting_AudioLink_Static)),
        };

        // Fields on the modern upstream RealtimeLight components we should NOT copy via name-match
        // because their values are Transform/Renderer references that point inside the source
        // prefab's hierarchy — copying them would have the new URP instance referencing upstream
        // children instead of its own. The URP prefab's own internal wiring is left intact.
        static readonly HashSet<string> INTERNAL_REF_FIELDS = new HashSet<string>
        {
            "panTransform", "tiltTransform", "lensTransform", "fixtureShellRenderers", "objRenderers",
        };

        // Position-equality tolerance for idempotency check (1mm).
        const float POSITION_EPSILON_SQR = 0.001f * 0.001f;

        [MenuItem(MENU_PATH)]
        static void Run()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                EditorUtility.DisplayDialog("VRSL Migration", "No active loaded scene.", "OK");
                return;
            }

            // Snapshot candidates before mutating — otherwise we'd walk into the URP siblings
            // we just inserted and try to migrate them too.
            var candidates = new List<(GameObject go, PrefabPair pair)>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive: true))
                {
                    if (!TryGetSourceGuid(t.gameObject, out string sourceGuid)) continue;
                    if (PAIRS_BY_UPSTREAM_GUID.TryGetValue(sourceGuid, out var pair))
                        candidates.Add((t.gameObject, pair));
                }
            }

            int created = 0, skipped = 0, errors = 0;
            var perKind = new Dictionary<string, int>();

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("VRSL: Add URP Siblings");
            int undoGroup = Undo.GetCurrentGroup();

            foreach (var (go, pair) in candidates)
            {
                if (HasUrpSiblingAlready(go.transform, pair.UrpGuid))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    var newGo = AddUrpSibling(go.transform, pair);
                    if (newGo != null)
                    {
                        created++;
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
            if (perKind.Count > 0)
                summary += "\n\nBy fixture:\n" + string.Join("\n",
                    perKind.OrderBy(k => k.Key).Select(k => $"  {k.Key}: {k.Value}"));

            Debug.Log("[VRSL Migration] " + summary.Replace("\n", "  "));
            EditorUtility.DisplayDialog("VRSL Migration", summary, "OK");
        }

        // ── In-place conversion (custom fixtures) ───────────────────────────────────

        /// <summary>
        /// Converts fixtures that carry an upstream VRSL component directly — e.g. a custom mesh
        /// (an imported lighting rig) with VRStageLighting_DMX_Static added onto it — rather than
        /// instances of a standard VRSL fixture prefab. The prefab-swap migration above can't help
        /// those: there's no source-prefab GUID in its table and no URP prefab carrying the custom
        /// geometry. This path keeps the geometry and retargets the driver: it adds the URP twin
        /// component (same field layout), copies every field across, swaps the fixture's VRSL
        /// materials to the matching URP shader, and removes the upstream component. Matching is by
        /// component, so nesting, unpacking, and prefab GUIDs are all irrelevant.
        ///
        /// Idempotent: a fixture that already has the URP twin is skipped, and material swaps key
        /// on the shader namespace (VRSL/… → VRSL-URP/…) so a second run is a no-op.
        /// </summary>
        [MenuItem(MENU_PATH_INPLACE)]
        static void RunInPlace()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                EditorUtility.DisplayDialog("VRSL In-Place Conversion", "No active loaded scene.", "OK");
                return;
            }

            // Snapshot first: we add and remove components as we go, so walking live would be unsafe.
            var candidates = new List<(GameObject go, Component upstream, Type urpType)>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var c in root.GetComponentsInChildren<Component>(includeInactive: true))
                {
                    if (c == null) continue;
                    string typeName = c.GetType().FullName;
                    foreach (var map in IN_PLACE_TYPES)
                    {
                        if (typeName == map.upstreamTypeName)
                            candidates.Add((c.gameObject, c, map.urpType));
                    }
                }
            }

            int converted = 0, skipped = 0, errors = 0;
            var seenMaterials = new HashSet<Material>();      // every material inspected once
            var changedMaterials = new HashSet<Material>();   // subset actually reshaded -> SaveAssets

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("VRSL: Convert Custom Fixtures In-Place");
            int undoGroup = Undo.GetCurrentGroup();

            foreach (var (go, upstream, urpType) in candidates)
            {
                if (upstream == null) continue;               // already destroyed this run
                if (go.GetComponent(urpType) != null)
                {
                    skipped++;
                    continue;
                }
                try
                {
                    var urp = Undo.AddComponent(go, urpType);
                    CopyAllFieldsByName(upstream, urp);
                    SwapFixtureMaterials(go, seenMaterials, changedMaterials);
                    Undo.DestroyObjectImmediate(upstream);
                    converted++;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[VRSL In-Place] Failed on '{go.name}': {ex}");
                    errors++;
                }
            }

            if (changedMaterials.Count > 0)
                AssetDatabase.SaveAssets();

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);

            string summary =
                $"Converted: {converted}\nSkipped (already URP): {skipped}\nErrors: {errors}\n" +
                $"Materials reshaded: {changedMaterials.Count}";
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

        // Idempotent check: any sibling of `source` (under the same parent, or any scene root
        // when `source` is itself a root) that is a URP-prefab instance and sits at the same
        // world position counts as already-migrated. The position match makes the check robust
        // to manual hierarchy reordering after a previous migration run.
        static bool HasUrpSiblingAlready(Transform source, string urpGuid)
        {
            var parent = source.parent;
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    var sibling = parent.GetChild(i);
                    if (sibling == source) continue;
                    if (IsMigratedSibling(sibling, source, urpGuid)) return true;
                }
            }
            else
            {
                foreach (var rootGo in source.gameObject.scene.GetRootGameObjects())
                {
                    var sibling = rootGo.transform;
                    if (sibling == source) continue;
                    if (IsMigratedSibling(sibling, source, urpGuid)) return true;
                }
            }
            return false;
        }

        static bool IsMigratedSibling(Transform sibling, Transform source, string urpGuid)
        {
            if (!TryGetSourceGuid(sibling.gameObject, out string siblingGuid)) return false;
            if (siblingGuid != urpGuid) return false;
            return (sibling.position - source.position).sqrMagnitude <= POSITION_EPSILON_SQR;
        }

        // ── Insertion ─────────────────────────────────────────────────────────────

        static GameObject AddUrpSibling(Transform source, PrefabPair pair)
        {
            string urpPath = AssetDatabase.GUIDToAssetPath(pair.UrpGuid);
            if (string.IsNullOrEmpty(urpPath))
            {
                Debug.LogWarning($"[VRSL Migration] URP prefab GUID {pair.UrpGuid} ({pair.FriendlyName}) is not in the AssetDatabase. Is the VRSL-URP package imported?");
                return null;
            }
            var urpAsset = AssetDatabase.LoadAssetAtPath<GameObject>(urpPath);
            if (urpAsset == null) return null;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(urpAsset, source.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(instance, "VRSL: Add URP Sibling");

            // Match world transform: parent under same node, copy local scale (since they
            // share a parent the world scale will match), then explicitly write world pos+rot.
            instance.transform.SetParent(source.parent, worldPositionStays: false);
            instance.transform.localScale = source.localScale;
            instance.transform.SetPositionAndRotation(source.position, source.rotation);
            instance.transform.SetSiblingIndex(source.GetSiblingIndex() + 1);

            instance.name = source.gameObject.name + " (URP)";

            CopyParameters(source.gameObject, instance, pair.Kind);
            return instance;
        }

        // ── Field translation ─────────────────────────────────────────────────────

        static void CopyParameters(GameObject source, GameObject target, FixtureKind kind)
        {
            switch (kind)
            {
                case FixtureKind.AudioLink: CopyAudioLinkParameters(source, target); break;
                case FixtureKind.Dmx:       CopyDmxParameters(source, target);       break;
            }
        }

        static void CopyAudioLinkParameters(GameObject source, GameObject target)
        {
            var dst = target.GetComponentInChildren<VRStageLighting_AudioLink_RealtimeLight>(includeInactive: true);
            if (dst == null) return;

            // Prefer modern upstream RealtimeLight if it's also present (effectively identical layout).
            var srcModern = FindComponentByTypeName(source, UPSTREAM_AL_REALTIME);
            if (srcModern != null)
            {
                CopyAllNamedFields(srcModern, dst, source);
                EditorUtility.SetDirty(dst);
                return;
            }

            var srcStatic = FindComponentByTypeName(source, UPSTREAM_AL_STATIC);
            if (srcStatic == null) return;

            // Direct copies (same name, compatible type — enums round-trip via int)
            CopyField(srcStatic, dst, "enableAudioLink");
            CopyField(srcStatic, dst, "band");
            CopyField(srcStatic, dst, "delay");
            CopyField(srcStatic, dst, "bandMultiplier");
            CopyField(srcStatic, dst, "globalIntensity");
            CopyField(srcStatic, dst, "finalIntensity");
            CopyField(srcStatic, dst, "textureSamplingCoordinates");
            CopyField(srcStatic, dst, "targetToFollow");

            // Renames
            CopyField(srcStatic, dst, srcName: "lightColorTint", dstName: "emissionColor");
            CopyField(srcStatic, dst, srcName: "selectGOBO",     dstName: "goboIndex");

            // Spin: only carry over the value when upstream's enableAutoSpin is true,
            // otherwise URP gets 0 (no spin) — matches upstream's runtime behaviour.
            bool autoSpin = ReadField(srcStatic, "enableAutoSpin", false);
            float spin    = ReadField(srcStatic, "spinSpeed",      0f);
            WriteField(dst, "goboSpinSpeed", autoSpin ? spin : 0f);

            // Color mode: collapse upstream's three boolean flags + themeColorTarget into our enum.
            // Upstream's themeColorTarget is 1..4; our enum values ThemeColor0..3 line up after -1.
            // Precedence (theme > chord > texture > emission) matches upstream's shader sampling order.
            bool theme = ReadField(srcStatic, "enableThemeColorSampling", false);
            bool chord = ReadField(srcStatic, "enableColorChord", false);
            bool tex   = ReadField(srcStatic, "enableColorTextureSampling", false);
            bool trad  = ReadField(srcStatic, "traditionalColorTextureSampling", false);
            int  themeIdx = ReadField(srcStatic, "themeColorTarget", 1);

            ALRealtimeColorMode mode;
            if (theme)      mode = ALRealtimeColorMode.ThemeColor0 + Mathf.Clamp(themeIdx - 1, 0, 3);
            else if (chord) mode = ALRealtimeColorMode.ColorChord;
            else if (tex)   mode = trad ? ALRealtimeColorMode.ColorTextureTraditional : ALRealtimeColorMode.ColorTexture;
            else            mode = ALRealtimeColorMode.Emission;
            dst.colorMode = mode;

            EditorUtility.SetDirty(dst);
        }

        static void CopyDmxParameters(GameObject source, GameObject target)
        {
            var dst = target.GetComponentInChildren<VRStageLighting_DMX_RealtimeLight>(includeInactive: true);
            if (dst == null) return;

            var srcModern = FindComponentByTypeName(source, UPSTREAM_DMX_REALTIME);
            if (srcModern != null)
            {
                CopyAllNamedFields(srcModern, dst, source);
                EditorUtility.SetDirty(dst);
                return;
            }

            var srcStatic = FindComponentByTypeName(source, UPSTREAM_DMX_STATIC);
            if (srcStatic == null) return;

            CopyField(srcStatic, dst, "enableDMXChannels");
            CopyField(srcStatic, dst, "enableFineChannels");
            CopyField(srcStatic, dst, "dmxChannel");
            CopyField(srcStatic, dst, "dmxUniverse");
            CopyField(srcStatic, dst, "useLegacySectorMode");
            CopyField(srcStatic, dst, "sector");
            CopyField(srcStatic, dst, "globalIntensity");
            CopyField(srcStatic, dst, "finalIntensity");
            CopyField(srcStatic, dst, "invertPan");
            CopyField(srcStatic, dst, "invertTilt");
            CopyField(srcStatic, dst, "enableStrobe");
            CopyField(srcStatic, dst, "maxMinPan");
            CopyField(srcStatic, dst, "maxMinTilt");

            CopyField(srcStatic, dst, srcName: "lightColorTint",     dstName: "shellEmissionTint");
            CopyField(srcStatic, dst, srcName: "tiltOffsetBlue",     dstName: "tiltOffset");
            CopyField(srcStatic, dst, srcName: "panOffsetBlueGreen", dstName: "panOffset");
            CopyField(srcStatic, dst, srcName: "enableAutoSpin",     dstName: "enableGoboSpin");

            EditorUtility.SetDirty(dst);
        }

        // ── Reflection helpers ────────────────────────────────────────────────────

        static Component FindComponentByTypeName(GameObject root, string fullName)
        {
            foreach (var c in root.GetComponentsInChildren<Component>(includeInactive: true))
            {
                if (c == null) continue;
                if (c.GetType().FullName == fullName) return c;
            }
            return null;
        }

        // Same-name field copy. For enums across different namespaces (e.g. VRSL.AudioLinkBandState
        // vs VRSL.URP.AudioLinkBandState) we round-trip via int since the enum members and ordinals
        // line up by design.
        static void CopyField(object src, object dst, string name) => CopyField(src, dst, name, name);

        static void CopyField(object src, object dst, string srcName, string dstName)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var srcField = src.GetType().GetField(srcName, FLAGS);
            var dstField = dst.GetType().GetField(dstName, FLAGS);
            if (srcField == null || dstField == null) return;

            object value = srcField.GetValue(src);
            var srcType = srcField.FieldType;
            var dstType = dstField.FieldType;

            if (value == null)
            {
                if (!dstType.IsValueType) dstField.SetValue(dst, null);
                return;
            }

            if (dstType.IsAssignableFrom(srcType))
            {
                dstField.SetValue(dst, value);
                return;
            }

            if (srcType.IsEnum && dstType.IsEnum)
            {
                int intVal = Convert.ToInt32(value);
                if (Enum.IsDefined(dstType, intVal))
                    dstField.SetValue(dst, Enum.ToObject(dstType, intVal));
                return;
            }

            try
            {
                dstField.SetValue(dst, Convert.ChangeType(value, dstType));
            }
            catch
            {
                // Mismatched types — leave the URP default in place.
            }
        }

        // For modern-RT-light → URP-RT-light migrations: copy every same-named field except
        // those whose values point to objects inside the source prefab (panTransform etc.).
        // Preserves the URP prefab's own internal wiring while migrating user-set scalars,
        // colours, enums, and external Transform references like targetToFollow.
        static void CopyAllNamedFields(object src, object dst, GameObject sourceRoot)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var df in dst.GetType().GetFields(FLAGS))
            {
                if (df.IsNotSerialized) continue;
                if (INTERNAL_REF_FIELDS.Contains(df.Name)) continue;

                var sf = src.GetType().GetField(df.Name, FLAGS);
                if (sf == null) continue;

                object value = sf.GetValue(src);

                // Belt-and-braces: if a Transform/Component/GameObject reference happens to point
                // inside the source's hierarchy, skip it — the URP prefab's own equivalent should
                // win. External references (e.g. a stage-mark Transform set by the author) carry over.
                if (value is UnityEngine.Object uo && uo != null && IsInsideSourceHierarchy(uo, sourceRoot))
                    continue;

                CopyField(src, dst, df.Name);
            }
        }

        // In-place twin copy: every serialized field by name, INCLUDING references into the
        // fixture's own hierarchy (objRenderers, transforms). Unlike CopyAllNamedFields nothing is
        // filtered — the URP twin lives on the same GameObject, so those references stay valid.
        // Arrays are cloned so the two components don't share one backing array before the upstream
        // component is removed.
        static void CopyAllFieldsByName(Component src, Component dst)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var df in dst.GetType().GetFields(FLAGS))
            {
                if (df.IsNotSerialized) continue;
                var sf = src.GetType().GetField(df.Name, FLAGS);
                if (sf == null) continue;

                if (df.FieldType.IsAssignableFrom(sf.FieldType))
                {
                    object v = sf.GetValue(src);
                    if (v is Array arr) v = arr.Clone();
                    df.SetValue(dst, v);
                }
                else
                {
                    CopyField(src, dst, df.Name);   // enum / Convert fallback for differing types
                }
            }
            EditorUtility.SetDirty(dst);
        }

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

        static bool IsInsideSourceHierarchy(UnityEngine.Object value, GameObject sourceRoot)
        {
            if (sourceRoot == null) return false;
            Transform t = value switch
            {
                GameObject g => g.transform,
                Component c  => c.transform,
                _            => null,
            };
            return t != null && t.IsChildOf(sourceRoot.transform);
        }

        static T ReadField<T>(object src, string name, T fallback)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var f = src.GetType().GetField(name, FLAGS);
            if (f == null) return fallback;
            object v = f.GetValue(src);
            if (v == null) return fallback;
            try { return (T)Convert.ChangeType(v, typeof(T)); }
            catch { return fallback; }
        }

        static void WriteField(object dst, string name, object value)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var f = dst.GetType().GetField(name, FLAGS);
            if (f == null) return;
            f.SetValue(dst, value);
        }

        // ── Data ──────────────────────────────────────────────────────────────────

        readonly struct PrefabPair
        {
            public readonly string UpstreamGuid;
            public readonly string UrpGuid;
            public readonly string FriendlyName;
            public readonly FixtureKind Kind;

            public PrefabPair(string upstream, string urp, string friendly, FixtureKind kind)
            {
                UpstreamGuid = upstream;
                UrpGuid = urp;
                FriendlyName = friendly;
                Kind = kind;
            }
        }

        enum FixtureKind { AudioLink, Dmx }
    }
}
#endif
