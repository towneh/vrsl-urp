#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// The manager inspector's wiring section: one line, a fold, and a repair.
    ///
    /// Ten fields on the DMX manager have exactly one correct value each, none of which
    /// an author can work out, and every one of them fails without an error. R-M7-4 asks
    /// for that to be a status line rather than ten questions.
    ///
    /// <b>Everything else is drawn exactly as Unity would.</b> The manager has 35 fields
    /// and this milestone touches ten; the artistic fields belong to the author and the
    /// cost knobs belong to M1. <c>DrawPropertiesExcluding</c> keeps the rest untouched,
    /// which is the difference between adding a section and starting a redesign.
    /// </summary>
    abstract class VRSLLightManagerEditor : Editor
    {
        const string FoldKey = "VRSL.Wiring.Fold";

        static bool Folded
        {
            get => SessionState.GetBool(FoldKey, false);
            set => SessionState.SetBool(FoldKey, value);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var fields = VRSLWiring.FieldsFor(target);
            if (fields != null)
            {
                DrawStatus(fields);
                DrawFold(fields);
                EditorGUILayout.Space();
            }

            // Everything that is not wiring, drawn the way it always was.
            var skip = new List<string> { "m_Script" };
            if (fields != null) skip.AddRange(fields.Select(f => f.Field));
            DrawPropertiesExcluding(serializedObject, skip.ToArray());

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// One line when everything is set up, and what the author will see when it is
        /// not.
        ///
        /// Read off the editor's own SerializedObject rather than through
        /// <see cref="VRSLWiring.Empty"/>: this runs on every repaint, and the inspector
        /// is not the place to be building objects or going near the AssetDatabase.
        /// </summary>
        void DrawStatus(VRSLWiringField[] fields)
        {
            var missing = fields
                .Where(f => serializedObject.FindProperty(f.Field) is { objectReferenceValue: null })
                .ToList();
            var faults = missing.Where(f => !f.Optional).ToList();
            var choices = missing.Where(f => f.Optional).ToList();

            if (missing.Count == 0)
            {
                EditorGUILayout.LabelField("Set up and ready.", EditorStyles.miniLabel);
                return;
            }

            // An empty optional field is a choice, not a fault, so it is stated rather
            // than warned about — this is the author who turned tile culling off, and a
            // warning that never goes away is one nobody reads by the third time.
            if (faults.Count == 0)
            {
                // The consequences are whole clauses — "strobe does nothing", "no beams
                // in the air" — so they read as a list of what is currently true, not as
                // a phrase to hang off "set up, with".
                EditorGUILayout.LabelField(
                    "Set up. Currently switched off, which is a choice you can make: "
                  + Join(choices.Select(f => f.Consequence)) + ".",
                    EditorStyles.wordWrappedMiniLabel);

                // Offered here as well as on the menu, because the author who wants one of
                // these back should not have to know where it lives to find it.
                if (GUILayout.Button("Turn these on"))
                {
                    Repair(targets);
                    GUIUtility.ExitGUI();
                }
                return;
            }

            // The consequence first and the field name after it, because the author is
            // looking at a scene that is wrong, not at a null reference. A list rather
            // than a count: two missing settings can go wrong in two unrelated ways, and
            // "2 settings are missing" tells nobody which of their problems this is.
            var text = new System.Text.StringBuilder(
                faults.Count == 1 ? "Something is missing, and this is what you will see:"
                                  : "Some things are missing, and this is what you will see:");
            foreach (var f in faults)
                text.Append("\n  • ").Append(Capitalise(f.Consequence)).Append(" (").Append(f.Field).Append(").");

            EditorGUILayout.HelpBox(text.ToString(), MessageType.Warning);

            if (GUILayout.Button("Set these up for me"))
            {
                Repair(targets);
                // The status above is built from the serialized state this repair just
                // changed, and IMGUI matches controls across passes rather than
                // tolerating a section that grows or shrinks mid-event.
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>
        /// The ten fields, behind a fold labelled for who opens it.
        ///
        /// Closed by default because an author has no reason to look, and present at all
        /// because somebody occasionally wants to point one somewhere else on purpose —
        /// which resolution then leaves alone.
        /// </summary>
        void DrawFold(VRSLWiringField[] fields)
        {
            Folded = EditorGUILayout.Foldout(
                Folded, "Assets this uses — filled in automatically", true);
            if (!Folded) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField(
                    "Set for you when the scene loads. Change one only if you mean to; "
                  + "anything you set is left alone.", EditorStyles.wordWrappedMiniLabel);

                foreach (var field in fields)
                {
                    var prop = serializedObject.FindProperty(field.Field);
                    if (prop != null) EditorGUILayout.PropertyField(prop);
                }
            }
        }

        /// <summary>
        /// Fill what is empty and say what happened, on however many managers are
        /// selected.
        ///
        /// It reports rather than acting in silence: a component that quietly fixes
        /// itself is one whose author never learns their scene was wrong, which is the
        /// first risk this milestone carries.
        /// </summary>
        internal static void Repair(IEnumerable<Object> managers)
        {
            var report = new System.Text.StringBuilder();
            int touched = 0;

            foreach (var manager in managers)
            {
                if (VRSLWiring.FieldsFor(manager) == null) continue;
                // No Undo.RecordObject here: Resolve writes through SerializedObject,
                // which registers its own undo. Recording as well would put two entries
                // on the stack for one repair, so undoing it would take two presses.
                // Everything, optional fields included: this is somebody asking, which
                // is exactly the case those are held back from the automatic path for.
                var result = VRSLWiring.Resolve(manager, includeOptional: true);
                touched++;
                report.Append(manager.name).Append(": ").AppendLine(result.Describe()).AppendLine();
            }

            EditorUtility.DisplayDialog(
                "VRSL wiring",
                touched == 0 ? "Nothing selected that has wiring to set up."
                             : report.ToString().TrimEnd(),
                "OK");
        }

        /// <summary>"a and b", "a, b and c" — a list an author reads rather than one a
        /// programmer joins.</summary>
        static string Join(IEnumerable<string> parts)
        {
            var all = parts.ToList();
            if (all.Count == 0) return "";
            if (all.Count == 1) return all[0];
            return string.Join(", ", all.Take(all.Count - 1)) + " and " + all[^1];
        }

        static string Capitalise(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    [CustomEditor(typeof(VRSL_URPLightManager))]
    [CanEditMultipleObjects]
    class VRSL_URPLightManagerEditor : VRSLLightManagerEditor { }

    [CustomEditor(typeof(VRSL_AudioLinkURPLightManager))]
    [CanEditMultipleObjects]
    class VRSL_AudioLinkURPLightManagerEditor : VRSLLightManagerEditor { }

    /// <summary>The repair, reachable without hunting for the component.</summary>
    static class VRSLWiringMenu
    {
        [MenuItem("VRSL/URP/Repair Manager Wiring")]
        static void RepairSelected()
        {
            // Asked for by type rather than by scanning every MonoBehaviour in the scene
            // and filtering. The scan reported no managers in a scene that plainly had
            // one, and it was the wrong shape anyway: a large scene has thousands of
            // components and two of them can be asked for directly.
            var managers = new List<Object>();
            managers.AddRange(Object.FindObjectsByType<VRSL_URPLightManager>(
                FindObjectsInactive.Include, FindObjectsSortMode.None));
            managers.AddRange(Object.FindObjectsByType<VRSL_AudioLinkURPLightManager>(
                FindObjectsInactive.Include, FindObjectsSortMode.None));

            if (managers.Count == 0)
            {
                EditorUtility.DisplayDialog("VRSL wiring",
                    "There is no VRSL light manager in this scene to set up.", "OK");
                return;
            }
            VRSLLightManagerEditor.Repair(managers);
        }
    }
}
#endif
