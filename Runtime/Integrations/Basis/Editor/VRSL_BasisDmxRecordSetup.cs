using UnityEditor;
using UnityEngine;

namespace VRSL.URP.BasisIntegration
{
    /// <summary>
    /// Adds a <see cref="BasisUserDataToVRSLDMX"/> and points it at the player.
    ///
    /// Thinner than its video-output sibling on purpose: the records arrive as
    /// bytes already addressed, so there is no framing to get wrong and nothing
    /// here that cannot be worked out by eye. What it does carry is the one
    /// thing about attaching it that surprises people, which is that from this
    /// point the grid is never read.
    /// </summary>
    static class VRSL_BasisDmxRecordSetup
    {
        const string Menu = "VRSL/URP/DMX Config/Add Basis DMX Record Source (SEI)";

        [MenuItem(Menu, true, 201)]
        static bool Validate() => Selection.activeGameObject != null;

        [MenuItem(Menu, false, 201)]
        static void AddAndBind()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            var source = go.GetComponent<BasisUserDataToVRSLDMX>();
            bool added = source == null;
            if (added) source = Undo.AddComponent<BasisUserDataToVRSLDMX>(go);
            else Undo.RecordObject(source, "Bind Basis DMX Record Source");

            var so = new SerializedObject(source);
            var player = so.FindProperty(nameof(BasisUserDataToVRSLDMX.Player));
            if (player.objectReferenceValue == null)
                player.objectReferenceValue = go.GetComponentInParent<BasisMediaPlayer>();

            so.ApplyModifiedProperties();
            if (PrefabUtility.IsPartOfPrefabInstance(source))
                PrefabUtility.RecordPrefabInstancePropertyModifications(source);
            Undo.SetCurrentGroupName(added ? "Add Basis DMX Record Source"
                                           : "Bind Basis DMX Record Source");
            Undo.CollapseUndoOperations(Undo.GetCurrentGroup());

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(source);

            if (player.objectReferenceValue == null)
            {
                Debug.LogWarning($"[VRSL] {(added ? "Added" : "Bound")} the DMX record source on "
                               + $"\"{go.name}\", but found no BasisMediaPlayer on it or above it. "
                               + "Assign one before playing.", source);
                return;
            }

            Debug.Log($"[VRSL] {(added ? "Added" : "Bound")} the DMX record source on "
                    + $"\"{go.name}\". Raise Minimum Universes to the show's size if you know "
                    + "it: the count grows when a block names a higher universe, and each "
                    + "growth reallocates the manager's buffers.", source);

            if (go.GetComponent<BasisVideoRenderTextureOutput>() != null)
                Debug.LogWarning($"[VRSL] \"{go.name}\" also frames a DMX grid into the RT. The "
                               + "records win from here: the manager reads the channel buffer "
                               + "whenever a source is publishing, and this one publishes from "
                               + "the moment it is enabled whether or not a record has arrived. "
                               + "On a path that re-encodes the video the buffer stays zeroed and "
                               + "the fixtures go dark rather than falling back to the picture, "
                               + "so a scene wanting the grid as a backstop has to disable this "
                               + "component itself when no records turn up.", source);
        }
    }
}
