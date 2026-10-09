// Editor-only, and in the runtime assembly beside VRSLWiring for the same reason: it reads
// the AssetDatabase, and the suite has to reach it.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRSL.URP
{
    /// <summary>
    /// Brings a DMX manager saved with 0.2.0-rc.6 across to <see cref="VRSLGridTextures"/>.
    /// </summary>
    /// <remarks>
    /// rc.6 managers named their five decode textures one by one, and the layout was
    /// whichever set those happened to be. The main texture survives as a hidden field,
    /// and is enough to tell which of the package's three layouts a manager was set up
    /// for. Without this a Vertical or Legacy world would come up reading the Horizontal
    /// grid and light the wrong fixtures, with no error anywhere. For one release only.
    /// </remarks>
    static class VRSLGridUpgrade
    {
        /// <summary>The hidden field the rc.6 main texture deserialises into.</summary>
        public const string OldField = "rc6MainTexture";

        /// <summary>Each layout's main decode texture as the package ships it.</summary>
        static readonly (string Guid, VRSLGridLayout Layout)[] ShippedMain =
        {
            ("555ce9e464c9a54c9d331e0030c258ef", VRSLGridLayout.Horizontal),
            ("78c70f9c712bd81bf5c13a40970fd79b", VRSLGridLayout.Vertical),
            ("17f83f063191dbffbaae01943cc9c235", VRSLGridLayout.Legacy),
        };

        public enum Outcome
        {
            /// <summary>Nothing from rc.6 on this manager.</summary>
            None,
            /// <summary>Set to the layout its old textures were, and the old field cleared.</summary>
            Upgraded,
            /// <summary>Pointed at textures of the author's own, which this cannot turn
            /// into an asset for them. Left as it was, and warned about.</summary>
            OwnTextures,
        }

        /// <summary>
        /// Upgrade one manager if it still carries an rc.6 main texture, and say what
        /// happened.
        /// </summary>
        /// <remarks>
        /// Through <see cref="SerializedObject"/>, like the wiring, so the change is undoable
        /// and lands as a prefab override where the manager is an instance.
        /// </remarks>
        public static Outcome Upgrade(VRSL_URPLightManager manager)
        {
            if (manager == null) return Outcome.None;

            var so  = new SerializedObject(manager);
            var old = so.FindProperty(OldField);
            if (old == null || old.objectReferenceValue == null) return Outcome.None;

            var oldTexture = old.objectReferenceValue;
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(oldTexture));

            foreach (var (shipped, layout) in ShippedMain)
            {
                if (guid != shipped) continue;
                so.FindProperty(nameof(VRSL_URPLightManager.dmxInput)).intValue = (int)layout;
                old.objectReferenceValue = null;
                so.ApplyModifiedProperties();
                // The change lands on whatever copy is open: the scene or prefab in the
                // editor, which keeps it once saved, or a Play-mode or build copy, which
                // never does. Until the source is saved this repeats on every load.
                Debug.Log($"[VRSL] {manager.name} now reads the {layout} grid, the layout its "
                        + "decode textures were set up for. Save the scene or prefab to keep "
                        + "the change; until then this happens again each time it loads.",
                          manager);
                return Outcome.Upgraded;
            }

            // The author's own textures. Once they have made an asset of their own and
            // pointed the manager at it, there is nothing left to say.
            var assigned = so.FindProperty(nameof(VRSL_URPLightManager.gridTextures)).objectReferenceValue;
            if (assigned != null && AssetDatabase.AssetPathToGUID(
                    AssetDatabase.GetAssetPath(assigned)) != VRSLWiring.GridTexturesGuid)
            {
                old.objectReferenceValue = null;
                so.ApplyModifiedProperties();
                return Outcome.Upgraded;
            }

            Debug.LogWarning(
                $"[VRSL] {manager.name} was set up with grid textures of your own "
              + $"({oldTexture.name}), and a manager now takes them as one Grid Textures "
              + "asset. Until you make one (Create → VRSL → URP → Grid Textures), fill in "
              + "your textures and set it as this manager's Grid Textures, it decodes the "
              + "video through the package's own Horizontal set instead.", manager);
            return Outcome.OwnTextures;
        }

        /// <summary>
        /// Upgrade and wire every DMX manager in a scene. For the built copy of a scene,
        /// which nobody may have opened since rc.6: nothing else runs on it before the
        /// player loads it.
        /// </summary>
        /// <remarks>
        /// Wiring as well as the upgrade, because an rc.6 scene's manager has no Grid
        /// Textures at all until something fills it, and a player never resolves anything
        /// itself.
        /// </remarks>
        public static void UpgradeScene(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            foreach (var manager in root.GetComponentsInChildren<VRSL_URPLightManager>(true))
            {
                Upgrade(manager);
                VRSLWiring.Resolve(manager);
            }
        }
    }
}
#endif
