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
    /// The wiring fields have exactly one correct value each, none of which an author
    /// can work out, and every one of them fails without an error, so they get a status
    /// line rather than a list of questions.
    ///
    /// <b>Everything else is drawn exactly as Unity would.</b> The artistic fields
    /// belong to the author. <c>DrawPropertiesExcluding</c> keeps the rest untouched,
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
                DrawUpgradeNotice();
                DrawFold(fields);
                EditorGUILayout.Space();
            }

            var skip = new List<string> { "m_Script" };
            if (fields != null) skip.AddRange(fields.Select(f => f.Field));
            skip.AddRange(DrawCost());

            // Everything else, drawn the way it always was and in the order it is
            // declared. A field added later lands here rather than nowhere, which is the
            // point of drawing the groups above by name and the rest by exclusion.
            DrawRest(skip);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>What <c>DrawPropertiesExcluding</c> draws, with the gobo fields drawn
        /// as one section where they are declared.</summary>
        void DrawRest(List<string> skip)
        {
            var it = serializedObject.GetIterator();
            for (bool enter = true; it.NextVisible(enter); enter = false)
            {
                if (skip.Contains(it.name)) continue;
                if (it.name == VRSLGoboWheelUpgrade.WheelField) { DrawGobos(it.Copy()); continue; }
                if (it.name == VRSLGoboWheelUpgrade.ListField) continue;
                EditorGUILayout.PropertyField(it, true);
            }
        }

        /// <summary>
        /// The wheel this manager links, or its own list when it links none, what DMX and
        /// AudioLink lights reach on it, and whether another manager in the scene changes
        /// what its lights show. The list is offered for editing only when no wheel is
        /// linked, since then it is what the lights use; otherwise the wheel is edited in
        /// the Gobo Wheel window.
        /// </summary>
        void DrawGobos(SerializedProperty wheel)
        {
            EditorGUILayout.PropertyField(wheel);
            var list = serializedObject.FindProperty(VRSLGoboWheelUpgrade.ListField);
            if (serializedObject.isEditingMultipleObjects || wheel.hasMultipleDifferentValues)
            {
                if (list != null) EditorGUILayout.PropertyField(list, true);
                return;
            }

            var asset = wheel.objectReferenceValue as VRSLGoboWheelAsset;
            int count;
            if (asset != null)
            {
                count = asset.gobos?.Length ?? 0;
                EditorGUILayout.LabelField($"{count} gobos from {asset.name}.", EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                EditorGUILayout.PropertyField(list, new GUIContent("Own Gobos", list.tooltip), true);
                count = list.arraySize;
                EditorGUILayout.LabelField(
                    "No wheel is linked, so this manager's lights use its own gobos above. To share them "
                  + "with every manager in the scene, make a wheel from them in the Gobo Wheel window.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            bool dmx = target is VRSL_DMXURPLightManager;
            EditorGUILayout.LabelField(
                dmx ? (count > VRSLGoboWheelAsset.DmxSlots
                        ? $"DMX lights choose from the first eight with the gobo channel; slots 9 to {count} "
                        + "are for AudioLink lights."
                        : "DMX lights choose a slot with the gobo channel.")
                    : "AudioLink lights pick a slot with Gobo Index.",
                EditorStyles.wordWrappedMiniLabel);

            string conflict = VRSLGoboWheelGUI.Conflict((MonoBehaviour)target);
            if (conflict != null) EditorGUILayout.HelpBox(conflict, MessageType.Warning);

            if (GUILayout.Button("Edit gobo wheel…")) VRSL_GoboWheelWindow.Open(asset);
        }

        /// <summary>Fields whose effect the quality level decides, in the order an
        /// author meets them.</summary>
        static readonly string[] LookFields =
        {
            "volumetricDensity", "volumetricAnisotropy", "volumetricTint",
            "volumetricIntensity", "coupleToSceneFog",
        };

        /// <summary>
        /// What DMX Input will read from: a channel source on standby under a grid, and
        /// under Stream data the source, or that there is none and the lights are dark.
        /// </summary>
        void DrawInputSource(SerializedProperty input)
        {
            if (input.hasMultipleDifferentValues || serializedObject.isEditingMultipleObjects) return;
            var manager = (VRSL_DMXURPLightManager)target;
            // In Play mode the source the manager actually holds; otherwise whichever the
            // scene would bind, which is the one a source finds on enable.
            var source = Application.isPlaying
                ? manager.ChannelSource as MonoBehaviour
                : VRSLGridUpgrade.ChannelSourceIn(manager.gameObject.scene);
            bool stream = input.enumValueIndex == (int)VRSLDmxInput.StreamData;

            if (!stream)
            {
                if (source != null)
                    EditorGUILayout.LabelField(
                        $"{source.name} is on standby. Choose Stream data to read it.",
                        EditorStyles.wordWrappedMiniLabel);
                return;
            }
            if (source != null)
                EditorGUILayout.LabelField($"Reads {source.name}.", EditorStyles.wordWrappedMiniLabel);
            else
                EditorGUILayout.HelpBox(
                    Application.isPlaying
                        ? "Nothing in this scene is sending stream data, so the lights are dark."
                        : "No source in this scene, so the lights will be dark. Add one from "
                        + "VRSL → URP → DMX Config, or choose a grid.",
                    MessageType.Warning);
        }

        /// <summary>
        /// What the package may spend, and the controls that level governs.
        /// </summary>
        /// <remarks>
        /// The look controls are drawn disabled at <c>Off</c> rather than hidden. An
        /// author who has switched the beams off and forgotten should be able to see the
        /// density and tint sitting there greyed out, rather than go looking for controls
        /// that have vanished and conclude the package is broken.
        ///
        /// Contact-shadow strength is in the same state for the same reason: <c>Off</c>
        /// carries no contact shadows, so the strength does nothing whatever it says.
        /// </remarks>
        /// <returns>The fields drawn here, so the caller does not draw them twice.</returns>
        IEnumerable<string> DrawCost()
        {
            var quality = serializedObject.FindProperty("quality");
            if (quality == null) return System.Array.Empty<string>();

            // Above quality because it decides whether anything lights at all. The DMX
            // manager only; the AudioLink one has no DMX input.
            var input = serializedObject.FindProperty(nameof(VRSL_DMXURPLightManager.dmxInput));
            if (input != null)
            {
                // Named here: Unity would title the field "Dmx Input".
                EditorGUILayout.PropertyField(input, new GUIContent("DMX Input", input.tooltip));
                DrawInputSource(input);
            }

            EditorGUILayout.PropertyField(quality);

            // On a multi-selection whose managers disagree, enumValueIndex is one of
            // them rather than a shared answer — so a single manager at Off would grey
            // the controls out for every other manager selected with it, and an author
            // editing several at once would find them uneditable for no visible reason.
            bool off = !quality.hasMultipleDifferentValues
                    && quality.enumValueIndex == (int)VRSLQuality.Off;
            if (off)
                EditorGUILayout.LabelField(
                    "Off: no beams and no contact shadows. These still set how they "
                  + "would look at Standard or High.",
                    EditorStyles.wordWrappedMiniLabel);

            var drawn = new List<string> { "quality" };
            if (input != null) drawn.Add(input.name);
            using (new EditorGUI.DisabledScope(off))
            {
                foreach (var name in LookFields)
                {
                    var property = serializedObject.FindProperty(name);
                    if (property == null) continue;   // the two managers differ
                    EditorGUILayout.PropertyField(property);
                    drawn.Add(name);
                }

                var strength = serializedObject.FindProperty("contactShadowStrength");
                if (strength != null)
                {
                    EditorGUILayout.PropertyField(strength);
                    drawn.Add("contactShadowStrength");
                }
            }

            EditorGUILayout.Space();
            return drawn;
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
                // The consequences are whole clauses ("strobe does nothing", "no beams
                // in the air"), so they read as a list of what is currently true, not as
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
        /// A DMX manager saved with separate grid texture fields holding textures of its
        /// author's own, until the author gives it a Grid Textures asset. The upgrade warns in the Console on every
        /// enable as well; this is where the author is looking when they come to fix it.
        /// </summary>
        void DrawUpgradeNotice()
        {
            if (serializedObject.isEditingMultipleObjects) return;
            var old = serializedObject.FindProperty(VRSLGridUpgrade.OldField);
            if (old == null || old.objectReferenceValue == null) return;
            EditorGUILayout.HelpBox(
                $"This manager was set up with grid textures of your own "
              + $"({old.objectReferenceValue.name}), and now takes them as one Grid Textures "
              + "asset. Until you make one (Create → VRSL → URP → Grid Textures), fill in "
              + "your textures and set it as Grid Textures under Asset references, the "
              + "video is decoded through the package's own textures instead.",
                MessageType.Warning);
        }

        /// <summary>
        /// This manager's wiring fields, behind a fold labelled for who opens it. The DMX
        /// manager has more than the AudioLink one, so nothing here may quote a count or
        /// name a field only one of them has.
        ///
        /// Closed by default because an author has no reason to look, and present at all
        /// because somebody occasionally wants to point one somewhere else on purpose,
        /// which resolution then leaves alone.
        /// </summary>
        void DrawFold(VRSLWiringField[] fields)
        {
            Folded = EditorGUILayout.Foldout(
                Folded, "Asset references", true);
            if (!Folded) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField(
                    "The required ones are set when the scene loads. The rest stay empty "
                  + "until you ask for them, because on those an empty box is how you turn "
                  + "that feature off. Anything you set yourself is left alone.",
                    EditorStyles.wordWrappedMiniLabel);

                foreach (var field in fields)
                {
                    var prop = serializedObject.FindProperty(field.Field);
                    if (prop != null) EditorGUILayout.PropertyField(prop);
                }

                var input = serializedObject.FindProperty(nameof(VRSL_DMXURPLightManager.dmxInput));
                if (input != null && !input.hasMultipleDifferentValues)
                    EditorGUILayout.LabelField(
                        input.enumValueIndex == (int)VRSLDmxInput.StreamData
                            ? "Grid Textures is not read under Stream data."
                            : $"Grid Textures is read for the {input.enumDisplayNames[input.enumValueIndex]}, "
                            + "which DMX Input sets.",
                        EditorStyles.wordWrappedMiniLabel);
            }
        }

        /// <summary>
        /// Fill what is empty and say what happened, on however many managers are
        /// selected.
        ///
        /// It reports rather than acting in silence: a component that silently fixes
        /// itself is one whose author never learns their scene was wrong, which is the
        /// main risk in letting a manager wire itself.
        ///
        /// <para>A dialog only when something could not be filled, because
        /// <c>EditorUtility.DisplayDialog</c> draws a warning icon whatever the message
        /// says and there is no API to change it. Reporting a clean repair through one
        /// puts a warning on a deliberate act that worked, which is exactly the
        /// choice-against-fault distinction the panel above spends its wording making.
        /// A clean repair says so in the Console and lets the panel flipping to "set up
        /// and ready" be the acknowledgement — it is under the cursor that just
        /// pressed the button. Same split as the automatic path, which warns only when
        /// something is unresolved.</para>
        /// </summary>
        internal static void Repair(IEnumerable<Object> managers)
        {
            var report = new System.Text.StringBuilder();
            int touched = 0;
            int stuck   = 0;

            foreach (var manager in managers)
            {
                if (VRSLWiring.FieldsFor(manager) == null) continue;
                // Also how a prefab asset whose manager still has separate grid texture
                // fields is brought across, since automatic resolution never touches assets.
                if (manager is VRSL_DMXURPLightManager dmx) VRSLGridUpgrade.Upgrade(dmx);
                if (manager is MonoBehaviour behaviour) VRSLGoboWheelUpgrade.Upgrade(behaviour);
                // No Undo.RecordObject here: Resolve writes through SerializedObject,
                // which registers its own undo. Recording as well would put two entries
                // on the stack for one repair, so undoing it would take two presses.
                // Everything, optional fields included: this is somebody asking, which
                // is exactly the case those are held back from the automatic path for.
                var result = VRSLWiring.Resolve(manager, includeOptional: true);
                touched++;
                stuck += result.Unresolved.Count;
                report.Append(manager.name).Append(": ").AppendLine(result.Describe()).AppendLine();
            }

            // Nothing to work on is worth a dialog of its own: the author asked for
            // something and it did not happen, and there is no panel to watch change.
            if (touched == 0)
            {
                EditorUtility.DisplayDialog(
                    "VRSL wiring", "Nothing selected that has wiring to set up.", "OK");
                return;
            }

            string text = report.ToString().TrimEnd();
            if (stuck > 0) EditorUtility.DisplayDialog("VRSL wiring", text, "OK");
            else           Debug.Log("[VRSL] " + text);
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

    [CustomEditor(typeof(VRSL_DMXURPLightManager))]
    [CanEditMultipleObjects]
    class VRSL_DMXURPLightManagerEditor : VRSLLightManagerEditor { }

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
            foreach (var manager in VRSLLightPaths.Managers(FindObjectsInactive.Include))
                if (manager is Object o) managers.Add(o);

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
