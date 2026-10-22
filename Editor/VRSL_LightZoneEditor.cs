using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// A Light Zone's inspector: what the zone does, its boxes, and the fixtures that stay
    /// inside it, in the author's words.
    /// </summary>
    [CustomEditor(typeof(VRSLLightZone))]
    class VRSL_LightZoneEditor : Editor
    {
        static readonly List<Component> s_lights = new();
        static bool s_stale = true;

        [InitializeOnLoadMethod]
        static void Watch()
        {
            EditorApplication.hierarchyChanged -= MarkStale;
            EditorApplication.hierarchyChanged += MarkStale;
        }

        static void MarkStale() => s_stale = true;

        /// <summary>Every realtime light in the open scenes whose zone is
        /// <paramref name="zone"/>, named or taken from a parent.</summary>
        static void LightsIn(VRSLLightZone zone, List<Component> into)
        {
            if (s_stale)
            {
                s_lights.Clear();
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                    if (mb is IVRSLRealtimeLight) s_lights.Add(mb);
                s_stale = false;
            }
            into.Clear();
            foreach (var light in s_lights)
            {
                if (light == null) continue;
                var named = new SerializedObject(light).FindProperty("lightZone")?.objectReferenceValue as VRSLLightZone;
                var resolved = named != null ? named : light.GetComponentInParent<VRSLLightZone>(true);
                if (resolved == zone) into.Add(light);
            }
        }

        readonly List<Component> _mine = new();

        public override void OnInspectorGUI()
        {
            VRSL_EditorHeader.Draw();
            var zone = (VRSLLightZone)target;

            EditorGUILayout.HelpBox(
                "Fixtures in this zone light only what is inside its boxes: surface light, beams, disco ball "
              + "dots and gobos. Put each box face that should stop the light inside the wall, not on its "
              + "surface. Nothing inside the zone blocks light, so a pillar in a beam still lets it through "
              + "to the wall behind.",
                MessageType.None);

            var boxes = zone.GetComponentsInChildren<VRSLLightZoneBox>(true);
            int on = 0;
            foreach (var box in boxes) if (box.isActiveAndEnabled) on++;
            EditorGUILayout.LabelField("Boxes", on == boxes.Length ? $"{on}" : $"{on} on, {boxes.Length - on} off");
            if (!zone.enabled || !zone.gameObject.activeInHierarchy)
                EditorGUILayout.HelpBox("Switched off, so its fixtures light as if they had no zone.", MessageType.Info);
            else if (on == 0)
                EditorGUILayout.HelpBox("No box is on, so this zone's fixtures light nothing.", MessageType.Warning);

            LightsIn(zone, _mine);
            EditorGUILayout.LabelField("Fixtures", _mine.Count == 0 ? "none" : $"{_mine.Count}");
            if (_mine.Count == 0)
                EditorGUILayout.HelpBox(
                    "No fixture stays inside this zone yet. Set a fixture's Zone to it, or put the fixtures "
                  + "under this GameObject.",
                    MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Box"))
            {
                var go = new GameObject("Light Zone Box");
                Undo.RegisterCreatedObjectUndo(go, "Add Light Zone Box");
                go.transform.SetParent(zone.transform, false);
                Undo.AddComponent<VRSLLightZoneBox>(go);
                Selection.activeGameObject = go;
            }
            using (new EditorGUI.DisabledScope(_mine.Count == 0))
                if (GUILayout.Button("Select Its Fixtures"))
                {
                    var objects = new Object[_mine.Count];
                    for (int i = 0; i < _mine.Count; i++) objects[i] = _mine[i].gameObject;
                    Selection.objects = objects;
                }
            EditorGUILayout.EndHorizontal();
        }
    }

    /// <summary>A Light Zone Box's inspector, with its faces draggable in the scene view.</summary>
    [CustomEditor(typeof(VRSLLightZoneBox)), CanEditMultipleObjects]
    class VRSL_LightZoneBoxEditor : Editor
    {
        readonly BoxBoundsHandle _handle = new();

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script");
            serializedObject.ApplyModifiedProperties();
            var box = (VRSLLightZoneBox)target;
            if (box.GetComponentInParent<VRSLLightZone>(true) == null)
                EditorGUILayout.HelpBox("Not under a Light Zone, so it limits nothing. Put it on the zone's GameObject "
                                      + "or a child of it.", MessageType.Warning);
        }

        void OnSceneGUI()
        {
            var box = (VRSLLightZoneBox)target;
            using (new Handles.DrawingScope(new Color(1f, 0.75f, 0.2f), box.transform.localToWorldMatrix))
            {
                _handle.center = box.center;
                _handle.size   = box.size;
                EditorGUI.BeginChangeCheck();
                _handle.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(box, "Resize Light Zone Box");
                    box.center = _handle.center;
                    box.size   = _handle.size;
                    VRSLLightZones.MarkChanged();
                }
            }
        }
    }
}
