using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Moves the open scene, or the prefab open in Prefab Mode, off the per-layout prefabs
    /// earlier releases shipped and onto the single fixture prefabs.
    /// </summary>
    static class VRSL_SupersededPrefabMenu
    {
        const string Title = "VRSL: Replace Superseded Prefabs";

        [MenuItem("VRSL/URP/Replace Superseded Prefabs in Scene", false, 122)]
        static void Run()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Scene scene = stage != null ? stage.scene : SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                EditorUtility.DisplayDialog(Title, "No scene is open.", "OK");
                return;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Title);
            int undoGroup = Undo.GetCurrentGroup();
            var result = VRSLSupersededPrefabs.ReplaceIn(scene);
            Undo.CollapseUndoOperations(undoGroup);
            // In Prefab Mode this marks the prefab for saving, which leaves the save to
            // Prefab Mode or the author and keeps the replacement undoable.
            if (result.Replaced > 0) EditorSceneManager.MarkSceneDirty(scene);

            string summary = result.Replaced == 0
                ? "Nothing to replace: no fixture or manager here comes from a superseded prefab."
                : $"Replaced {result.Replaced}, keeping each one's own settings:\n" + string.Join("\n",
                    result.ByPrefab.OrderBy(k => k.Key).Select(k => $"  {k.Key}: {k.Value}"));
            if (result.InsideOtherPrefabs > 0)
                summary += $"\n\n{result.InsideOtherPrefabs} more sit inside other prefabs. Open each of those " +
                           "prefabs and run this again there.";
            Debug.Log("[VRSL] " + summary.Replace("\n", "  "));
            EditorUtility.DisplayDialog(Title, summary, "OK");
        }
    }
}
