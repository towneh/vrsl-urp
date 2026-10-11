#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Suppresses "missing script" noise on VRSL fixtures carrying component
    /// slots whose scripts aren't in the project.
    ///
    /// A scene saved with an earlier release, or migrated from the original
    /// VRSL package and then left without it, can hold fixtures whose UdonSharp
    /// or VRChat components no longer bind. Every such slot logs a missing-script
    /// warning. The unbound components can't do anything here anyway, but the
    /// warnings clutter the console and fail Play Mode entry checks.
    ///
    /// This utility walks each scene as it loads and silently removes the
    /// missing-script slots from any GameObject in a VRSL subtree. Once a
    /// VRSL-namespaced component is found anywhere in the tree, the entire
    /// descendant subtree is treated as VRSL territory and its missing-script
    /// slots are scrubbed.
    ///
    /// It does NOT modify the prefab or scene assets on disk: scenes aren't
    /// marked dirty, so no save prompts appear. Cleanup runs again on every
    /// scene open.
    /// </summary>
    [InitializeOnLoad]
    internal static class VRSL_MissingScriptCleaner
    {
        static VRSL_MissingScriptCleaner()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;
            // delayCall covers the scene that was already loaded at editor
            // startup or domain reload — sceneOpened doesn't fire for those.
            EditorApplication.delayCall += CleanAllOpenScenes;
        }

        static void OnSceneOpened(Scene scene, OpenSceneMode mode) => CleanScene(scene);

        static void CleanAllOpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
                CleanScene(SceneManager.GetSceneAt(i));
        }

        static void CleanScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            foreach (var root in scene.GetRootGameObjects())
                CleanRecursive(root, insideVRSLSubtree: false);
        }

        // Once a VRSL-namespaced component is found anywhere on the path from
        // a scene root down to this GameObject, every descendant from that
        // point on is treated as VRSL territory and gets its missing-script
        // slots scrubbed. This covers prefabs whose VRSL component sits on
        // the root while child GameObjects carry script references that go
        // missing in this URP-only package.
        static void CleanRecursive(GameObject go, bool insideVRSLSubtree)
        {
            bool inSubtree = insideVRSLSubtree || HasVRSLComponent(go);

            if (inSubtree)
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);

            foreach (Transform child in go.transform)
                CleanRecursive(child.gameObject, inSubtree);
        }

        // Restricts cleanup to subtrees rooted under a recognisable VRSL
        // GameObject so unrelated missing scripts the user is investigating
        // elsewhere in the scene are left alone. Recognised by either the
        // VRSL namespace or the conventional VRSL_ / VRStageLighting_ type-
        // name prefix — a few VRSL scripts live in the global namespace and
        // would otherwise be missed.
        static bool HasVRSLComponent(GameObject go)
        {
            var comps = go.GetComponents<MonoBehaviour>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;            // missing script
                var t = c.GetType();
                var ns = t.Namespace;
                if (ns != null && (ns == "VRSL" || ns.StartsWith("VRSL.")))
                    return true;
                var name = t.Name;
                if (name.StartsWith("VRSL_") || name.StartsWith("VRStageLighting_"))
                    return true;
            }
            return false;
        }
    }
}
#endif
