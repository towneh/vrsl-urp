// Editor-only, and in the runtime assembly beside VRSLMigrationTable so the suite can
// drive it: the test assembly does not reference the editor one.
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRSL.URP
{
    /// <summary>
    /// Moves instances of this package's superseded prefabs onto the prefab that replaces
    /// each, keeping the instance's overrides (address, intensity, pose, anything else set on
    /// it) wherever the two prefabs line up.
    /// </summary>
    static class VRSLSupersededPrefabs
    {
        public struct Result
        {
            public int Replaced;
            /// <summary>Instances inside another prefab, which only that prefab's own
            /// Prefab Mode can change.</summary>
            public int InsideOtherPrefabs;
            public Dictionary<string, int> ByPrefab;
        }

        public static Result ReplaceIn(Scene scene)
        {
            var result = new Result { ByPrefab = new Dictionary<string, int>() };
            var candidates = new List<(GameObject go, GameObject replacement)>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive: true))
                {
                    var go = t.gameObject;
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(go)) continue;
                    var replacement = ReplacementFor(go);
                    if (replacement == null) continue;
                    if (PrefabUtility.GetOutermostPrefabInstanceRoot(go) != go) result.InsideOtherPrefabs++;
                    else candidates.Add((go, replacement));
                }
            }

            foreach (var (go, replacement) in candidates)
            {
                string from = PrefabUtility.GetCorrespondingObjectFromSource(go).name;
                Replace(go, replacement);
                result.Replaced++;
                result.ByPrefab.TryGetValue(from, out int n);
                result.ByPrefab[from] = n + 1;
            }
            return result;
        }

        /// <summary>The prefab replacing the one <paramref name="instanceRoot"/> is a direct
        /// instance of, or null when it isn't a superseded prefab. An instance of an author's
        /// own variant of one is left alone: the variant is theirs.</summary>
        public static GameObject ReplacementFor(GameObject instanceRoot)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot);
            if (source == null) return null;
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            if (!VRSLMigrationTable.Superseded.TryGetValue(guid, out string replacementGuid)) return null;
            return AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(replacementGuid));
        }

        public static void Replace(GameObject instanceRoot, GameObject replacement)
        {
            string oldName = PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot).name;

            // A superseded manager's input is its prefab's default, not an override, so the
            // swap alone would put every Legacy and Vertical manager on the Horizontal grid.
            var manager = instanceRoot.GetComponent<VRSL_URPLightManager>();
            VRSLDmxInput? input = manager != null ? manager.dmxInput : (VRSLDmxInput?)null;

            var settings = new PrefabReplacingSettings
            {
                objectMatchMode = ObjectMatchMode.ByHierarchy,
                prefabOverridesOptions = PrefabOverridesOptions.KeepAllPossibleOverrides,
                changeRootNameToAssetName = instanceRoot.name == oldName,
                logInfo = false,
            };
            PrefabUtility.ReplacePrefabAssetOfPrefabInstance(instanceRoot, replacement, settings, InteractionMode.UserAction);

            manager = instanceRoot.GetComponent<VRSL_URPLightManager>();
            if (input.HasValue && manager != null && manager.dmxInput != input.Value)
            {
                Undo.RecordObject(manager, "VRSL: Keep DMX Input");
                manager.dmxInput = input.Value;
                PrefabUtility.RecordPrefabInstancePropertyModifications(manager);
            }
        }
    }
}
#endif
