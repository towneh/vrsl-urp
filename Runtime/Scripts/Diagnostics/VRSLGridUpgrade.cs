// Editor-only, and in the runtime assembly beside VRSLWiring for the same reason: it reads
// the AssetDatabase, and the suite has to reach it.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VRSL.URP
{
    /// <summary>
    /// Brings a DMX manager saved with 0.2.0-rc.6 across to <see cref="VRSLGridTextures"/>,
    /// and decides once what a manager not yet looked at reads.
    /// </summary>
    /// <remarks>
    /// rc.6 managers named their five decode textures one by one, and the layout was
    /// whichever set those happened to be. The main texture survives as a hidden field,
    /// and is enough to tell which of the package's three layouts a manager was set up
    /// for. Without this a Vertical or Legacy world would come up reading the Horizontal
    /// grid and light the wrong fixtures, with no error anywhere. For one release only.
    ///
    /// 0.2.0-rc.6 read a channel source whenever one was sending, so a manager not yet
    /// looked at whose scene holds one is brought up on Stream data.
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
        /// Upgrade one manager if it still carries an rc.6 main texture or hasn't been
        /// looked at, and say what happened to the rc.6 texture.
        /// </summary>
        /// <remarks>
        /// Through <see cref="SerializedObject"/>, like the wiring, so the change is undoable
        /// and lands as a prefab override where the manager is an instance.
        /// </remarks>
        public static Outcome Upgrade(VRSL_DMXURPLightManager manager)
        {
            if (manager == null) return Outcome.None;

            var so      = new SerializedObject(manager);
            var outcome = UpgradeRc6(manager, so);
            UpgradeInput(manager, so);
            return outcome;
        }

        /// <summary>The hidden field saying whether this has looked at a manager yet.</summary>
        public const string VersionField = nameof(VRSL_DMXURPLightManager.dmxInputVersion);

        /// <summary>
        /// A manager not yet looked at, saved with 0.2.0-rc.6 or added since, comes up on
        /// Stream data when its scene holds a channel source: 0.2.0-rc.6 read such a source
        /// whenever it was publishing, so a world already lit from SEI or OSC stays lit.
        /// Runs once per manager, after the rc.6 rule, which may have set its layout.
        /// </summary>
        static void UpgradeInput(VRSL_DMXURPLightManager manager, SerializedObject so)
        {
            var version = so.FindProperty(VersionField);
            if (version == null || version.intValue >= VRSL_DMXURPLightManager.CurrentDmxInputVersion) return;

            var source = ChannelSourceIn(manager.gameObject.scene);
            if (source != null)
            {
                so.FindProperty(nameof(VRSL_DMXURPLightManager.dmxInput)).intValue = (int)VRSLDmxInput.StreamData;
                Debug.Log($"[VRSL] {manager.name} reads Stream data from {source.name}, the channel "
                        + "source in its scene. Choose a grid under DMX Input to read the video instead. "
                        + "Save the scene to keep the change; until then this happens again each time it "
                        + "loads.", manager);
            }
            version.intValue = VRSL_DMXURPLightManager.CurrentDmxInputVersion;
            so.ApplyModifiedProperties();
        }

        /// <summary>An enabled channel source component in <paramref name="scene"/>, or
        /// null. A prefab asset has no scene, so it never has one.</summary>
        internal static MonoBehaviour ChannelSourceIn(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour is IVRSLDMXChannelSource && behaviour.enabled
                    && behaviour.gameObject.activeInHierarchy)
                    return behaviour;
            return null;
        }

        static Outcome UpgradeRc6(VRSL_DMXURPLightManager manager, SerializedObject so)
        {
            var old = so.FindProperty(OldField);
            if (old == null || old.objectReferenceValue == null) return Outcome.None;

            var oldTexture = old.objectReferenceValue;
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(oldTexture));

            foreach (var (shipped, layout) in ShippedMain)
            {
                if (guid != shipped) continue;
                so.FindProperty(nameof(VRSL_DMXURPLightManager.dmxInput)).intValue = (int)layout;
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
            var assigned = so.FindProperty(nameof(VRSL_DMXURPLightManager.gridTextures)).objectReferenceValue;
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
            foreach (var manager in root.GetComponentsInChildren<VRSL_DMXURPLightManager>(true))
            {
                Upgrade(manager);
                VRSLWiring.Resolve(manager);
            }
        }
    }
}
#endif
