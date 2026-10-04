using UnityEditor;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// The package header over the component's own fields, and in Play mode a
    /// live block saying whether anything is arriving and what became of it,
    /// so "nothing is lighting" has a reading rather than a guess.
    /// </summary>
    [CustomEditor(typeof(VRSLTrussOscDmxSource))]
    class VRSL_TrussOscDmxSourceEditor : Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            VRSL_EditorHeader.Draw();
            DrawPropertiesExcluding(serializedObject, "m_Script");
            serializedObject.ApplyModifiedProperties();

            var source = (VRSLTrussOscDmxSource)target;
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Listens in Play mode. Run truss-relay with --osc <this machine>:"
                  + $"{source.port} and the fixtures follow the desk with no stream playing.",
                    MessageType.None);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);
            if (!source.Listening)
            {
                EditorGUILayout.HelpBox(
                    "Not listening, so nothing can arrive. "
                  + (source.LastError ?? "The socket is closed."),
                    MessageType.Error);
                return;
            }

            EditorGUILayout.LabelField("Listening on", $"{source.listenAddress}:{source.port}");
            EditorGUILayout.LabelField("Datagrams",
                $"{source.DatagramsReceived} received, {source.DatagramsIgnored} not "
              + VRSLTrussOscDmxSource.Address);
            EditorGUILayout.LabelField("Records",
                $"{source.RecordsDecoded} decoded, {source.RecordsDropped} dropped");
            EditorGUILayout.LabelField("Last result", source.LastResult.ToString());
            EditorGUILayout.LabelField("Universes", source.UniverseCount.ToString());

            if (source.DatagramsReceived == 0)
                EditorGUILayout.HelpBox(
                    "Nothing has arrived yet. Check truss-relay is running with --osc pointed "
                  + "at this machine and this port, and that a desk is sending it Art-Net.",
                    MessageType.Info);
            else if (source.RecordsDecoded == 0 && source.DatagramsIgnored > 0)
                EditorGUILayout.HelpBox(
                    "Datagrams are arriving but none is a Truss record. Something else is "
                  + "sending to this port; give the relay and this source a port of their own.",
                    MessageType.Warning);
            else if (source.RecordsDropped > 0 && source.RecordsDecoded == 0)
                EditorGUILayout.HelpBox(
                    $"Every record so far was dropped ({source.LastResult}). The relay and this "
                  + "package disagree about the record format, or the bytes are damaged in "
                  + "transit; the DMX Monitor and truss-detect osc:// will say which.",
                    MessageType.Warning);
        }
    }

    /// <summary>
    /// Adds a <see cref="VRSLTrussOscDmxSource"/> to the selected object, the
    /// way the Basis integration's menu adds its SEI source.
    /// </summary>
    static class VRSL_TrussOscDmxSourceSetup
    {
        const string Menu = "VRSL/URP/DMX Config/Add Truss OSC DMX Source";

        [MenuItem(Menu, true, 202)]
        static bool Validate() => Selection.activeGameObject != null;

        [MenuItem(Menu, false, 202)]
        static void Add()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;
            var existing = go.GetComponent<VRSLTrussOscDmxSource>();
            var source = existing != null ? existing : Undo.AddComponent<VRSLTrussOscDmxSource>(go);
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(source);
            Debug.Log($"[VRSL] {(existing != null ? "Found" : "Added")} the Truss OSC DMX Source on "
                    + $"\"{go.name}\". Run truss-relay with --osc <this machine>:{source.port} and "
                    + "press Play; raise Minimum Universes to the show's size if you know it.", source);
        }
    }
}
