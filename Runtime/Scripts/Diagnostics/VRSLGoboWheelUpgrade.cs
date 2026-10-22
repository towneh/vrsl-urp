// Editor-only, and in the runtime assembly beside VRSLGridUpgrade for the same reason: it
// reads the AssetDatabase, both managers call it, and the suite has to reach it.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRSL.URP
{
    /// <summary>
    /// The package's default gobo wheel, and the look each light manager gets once to
    /// decide whether it uses it.
    /// </summary>
    /// <remarks>
    /// The shipped manager prefabs link the default wheel, so every manager made from one
    /// picks it up, including managers saved before wheels existed. One whose own gobo list
    /// isn't upstream's eight in order was set up with gobos of its author's own: the
    /// default is taken off it so that list keeps working, and the author is told how to
    /// share it. Both managers carry the same three fields, so this works on either through
    /// <see cref="SerializedObject"/>, which also makes the change undoable and lands it as
    /// a prefab override where the manager is an instance.
    /// </remarks>
    static class VRSLGoboWheelUpgrade
    {
        public const string WheelField   = "goboWheel";
        public const string ListField    = "goboTextures";
        public const string VersionField = "goboWheelVersion";

        /// <summary>0 until this has looked at a manager. Never 1 in a prefab: a saved
        /// scene's manager inherits what its prefab says, and would never be looked at.</summary>
        public const int CurrentVersion = 1;

        public const string DefaultWheelGuid = "2bbe85cfce714b1b80bfaa65bae8e18d";

        /// <summary>Upstream VRSL's eight gobos in the order its managers load them:
        /// Default, 5PointStar, 8PointStar, ApolloOrb, Spiral, Sticks, Swooshes,
        /// Triangles.</summary>
        public static readonly string[] UpstreamEightGuids =
        {
            "f7599b8713278f4c0b0bd11fcd836c6a",
            "6f40818edcd035ed4078ecc48e774ba5",
            "b42afc20e67f0e89c44982e0d425e994",
            "9bd273a7a95d3815ed7a912cccbb70da",
            "222d7fbeec533e190da7b9e40e56e7da",
            "c9ce909bb29e6898ea7808fd7d8196ff",
            "de2e2f340b0b44b4946a9bd5f7e52697",
            "f8f0801130c0a1d0e3862e4ad9106fe1",
        };

        /// <summary>The wheel the package ships, or null if it can't be found.</summary>
        public static VRSLGoboWheelAsset LoadDefault() =>
            AssetDatabase.LoadAssetAtPath<VRSLGoboWheelAsset>(AssetDatabase.GUIDToAssetPath(DefaultWheelGuid));

        public static bool IsDefault(Object wheel) =>
            wheel != null && AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(wheel)) == DefaultWheelGuid;

        /// <summary>Whether <paramref name="gobos"/> is upstream's eight, in order.</summary>
        public static bool IsUpstreamEight(Texture2D[] gobos)
        {
            if (gobos == null || gobos.Length != UpstreamEightGuids.Length) return false;
            for (int i = 0; i < gobos.Length; i++)
                if (gobos[i] == null
                    || AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(gobos[i])) != UpstreamEightGuids[i])
                    return false;
            return true;
        }

        static Texture2D[] ListOf(SerializedProperty list)
        {
            var gobos = new Texture2D[list.arraySize];
            for (int i = 0; i < gobos.Length; i++)
                gobos[i] = list.GetArrayElementAtIndex(i).objectReferenceValue as Texture2D;
            return gobos;
        }

        /// <summary>
        /// Link the default wheel and fill the manager's own list with upstream's eight,
        /// which is what a manager made from the prefab holds. A manager's Reset and its
        /// Load Default Gobo Wheel menu item both come here.
        /// </summary>
        public static void LoadDefault(MonoBehaviour manager)
        {
            if (manager == null) return;
            var wheel = LoadDefault();
            var so = new SerializedObject(manager);
            so.FindProperty(WheelField).objectReferenceValue = wheel;
            var list = so.FindProperty(ListField);
            list.arraySize = UpstreamEightGuids.Length;
            for (int i = 0; i < UpstreamEightGuids.Length; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(UpstreamEightGuids[i]));
            so.FindProperty(VersionField).intValue = CurrentVersion;
            so.ApplyModifiedProperties();
        }

        public enum Outcome
        {
            /// <summary>Already looked at, or nothing to decide.</summary>
            None,
            /// <summary>Kept the wheel it links.</summary>
            Kept,
            /// <summary>Its own list wasn't upstream's eight, so the default wheel was taken
            /// off it and the list is in charge.</summary>
            OwnList,
        }

        /// <summary>Look at one manager if it hasn't been looked at yet.</summary>
        public static Outcome Upgrade(MonoBehaviour manager)
        {
            if (manager == null) return Outcome.None;
            var so = new SerializedObject(manager);
            var version = so.FindProperty(VersionField);
            if (version == null || version.intValue >= CurrentVersion) return Outcome.None;

            var wheel   = so.FindProperty(WheelField);
            var list    = so.FindProperty(ListField);
            var outcome = Outcome.Kept;
            if (IsDefault(wheel.objectReferenceValue) && !IsUpstreamEight(ListOf(list)))
            {
                wheel.objectReferenceValue = null;
                outcome = Outcome.OwnList;
                Debug.Log($"[VRSL] {manager.name} has gobos of its own ({list.arraySize} in its list), so it "
                        + "keeps them and doesn't use the package's default gobo wheel. Its lights look as "
                        + "they did. To give every manager in the scene the same gobos, open VRSL → URP → "
                        + "Gobo Wheel and make a wheel from this list. Save the scene to keep the change; "
                        + "until then this happens again each time it loads.", manager);
            }
            version.intValue = CurrentVersion;
            so.ApplyModifiedProperties();
            return outcome;
        }

        /// <summary>Look at every light manager in a scene, for its built copy.</summary>
        public static void UpgradeScene(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour is IVRSLLightManager) Upgrade(behaviour);
        }
    }
}
#endif
