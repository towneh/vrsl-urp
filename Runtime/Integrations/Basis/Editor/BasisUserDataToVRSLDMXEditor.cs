using UnityEditor;
using UnityEngine;

namespace VRSL.URP.BasisIntegration
{
    /// <summary>
    /// The package header over the component's own fields. It has no custom
    /// drawing of its own to do — the fields are a player reference, a universe
    /// count and a logging toggle — so this exists to say which package the
    /// component came from, the way every other VRSL inspector does.
    /// </summary>
    [CustomEditor(typeof(BasisUserDataToVRSLDMX))]
    public class BasisUserDataToVRSLDMX_Editor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            VRSL_EditorHeader.Draw();

            // Everything but the script line, which the header replaces.
            DrawPropertiesExcluding(serializedObject, "m_Script");

            serializedObject.ApplyModifiedProperties();
        }
    }
}
