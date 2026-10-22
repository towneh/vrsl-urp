#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// What the open scenes' light managers do with their gobos, and the slot editor the
    /// Gobo Wheel window, the wheel asset's inspector and the managers' inspectors share.
    /// </summary>
    static class VRSLGoboWheelGUI
    {
        /// <summary>One manager and the gobos its lights would index.</summary>
        internal struct ManagerWheel
        {
            public MonoBehaviour      Manager;
            public bool               Dmx;
            public VRSLGoboWheelAsset Asset;
            public Texture2D[]        Gobos;
        }

        static List<ManagerWheel> s_cache = new();
        static double s_cachedAt = -1;

        /// <summary>Every switched-on light manager in the open scenes. Looked up at most
        /// once a second: the inspectors ask on every repaint.</summary>
        internal static List<ManagerWheel> SceneManagers(bool fresh = false)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!fresh && s_cachedAt >= 0 && now - s_cachedAt < 1.0) return s_cache;
            s_cachedAt = now;
            s_cache = new List<ManagerWheel>();
            foreach (var m in VRSLLightPaths.Managers(FindObjectsInactive.Exclude))
            {
                if (m is not MonoBehaviour behaviour || behaviour == null || !behaviour.isActiveAndEnabled) continue;
                if (m is not IVRSLSceneResourceUser user) continue;
                var so = new SerializedObject(behaviour);
                s_cache.Add(new ManagerWheel
                {
                    Manager = behaviour,
                    Dmx     = user.TakesPrecedence,
                    Asset   = so.FindProperty(VRSLGoboWheelUpgrade.WheelField)?.objectReferenceValue as VRSLGoboWheelAsset,
                    Gobos   = user.Gobos,
                });
            }
            return s_cache;
        }

        /// <summary>The gobos the scene's lights show: the DMX manager's where it has any,
        /// otherwise the AudioLink manager's, as <see cref="VRSLSceneResources"/> chooses.</summary>
        internal static ManagerWheel? Used(List<ManagerWheel> managers)
        {
            var users = new List<IVRSLSceneResourceUser>();
            foreach (var m in managers) users.Add((IVRSLSceneResourceUser)m.Manager);
            var (gobos, _) = VRSLSceneResources.Choose(users);
            if (gobos == null) return null;
            foreach (var m in managers)
                if (ReferenceEquals(m.Gobos, gobos)) return m;
            return null;
        }

        internal static string Source(ManagerWheel m) =>
            m.Asset != null ? $"the {m.Asset.name} wheel" : "its own gobo list";

        /// <summary>What an author needs to know when <paramref name="manager"/>'s lights
        /// won't show its own gobos, or will show its gobos on another manager's lights.
        /// Null when the managers agree.</summary>
        internal static string Conflict(MonoBehaviour manager)
        {
            var managers = SceneManagers();
            if (managers.Count < 2) return null;
            var used = Used(managers);
            if (used == null) return null;
            ManagerWheel? self = null;
            foreach (var m in managers) if (m.Manager == manager) self = m;
            if (self == null) return null;

            foreach (var other in managers)
            {
                if (other.Manager == manager || VRSLSceneResources.SameWheel(other.Gobos, self.Value.Gobos)) continue;
                if (used.Value.Manager == manager)
                    return $"{other.Manager.name} has different gobos ({Source(other)}). One wheel serves the "
                         + $"whole scene and it's this manager's, so {other.Manager.name}'s lights show these "
                         + "gobos rather than its own. Give both managers the same wheel in VRSL → URP → "
                         + "Gobo Wheel.";
                if (used.Value.Manager == other.Manager)
                    return $"{other.Manager.name} has different gobos ({Source(other)}). One wheel serves the "
                         + $"whole scene and it's {other.Manager.name}'s"
                         + (other.Dmx ? ", because DMX shows are programmed against it" : "")
                         + $", so this manager's lights show its gobos rather than these. Give both managers "
                         + "the same wheel in VRSL → URP → Gobo Wheel.";
            }
            return null;
        }

        internal static string SlotLabel(int slot) =>
            VRSLGoboWheelAsset.DmxRange(slot) is { } r ? $"DMX {r.lo}–{r.hi}" : "AudioLink only";

        // ── The slot editor ───────────────────────────────────────────────────

        static readonly Dictionary<SerializedObject, ReorderableList> s_lists = new();

        /// <summary>The wheel's slots: thumbnail, name and DMX range, to reorder, add and
        /// remove. Read-only for the package's own wheel.</summary>
        internal static void DrawSlots(SerializedObject wheel, bool readOnly)
        {
            var gobos = wheel.FindProperty(nameof(VRSLGoboWheelAsset.gobos));
            if (!s_lists.TryGetValue(wheel, out var list) || list.serializedProperty != gobos)
            {
                if (s_lists.Count > 32) s_lists.Clear();
                list = new ReorderableList(wheel, gobos, true, true, true, true)
                {
                    elementHeight = 44f,
                };
                list.drawHeaderCallback = rect => EditorGUI.LabelField(rect,
                    "Slots. DMX reaches the first eight; AudioLink lights reach every slot.");
                list.drawElementCallback = (rect, index, active, focused) =>
                {
                    var element = gobos.GetArrayElementAtIndex(index);
                    var thumb = new Rect(rect.x, rect.y + 2f, 40f, 40f);
                    var nameRect = new Rect(rect.x + 48f, rect.y + 4f, rect.width - 48f, EditorGUIUtility.singleLineHeight);
                    var rangeRect = new Rect(rect.x + 48f, rect.y + 4f + EditorGUIUtility.singleLineHeight,
                                             rect.width - 48f, EditorGUIUtility.singleLineHeight);
                    element.objectReferenceValue = EditorGUI.ObjectField(
                        thumb, element.objectReferenceValue, typeof(Texture2D), false);
                    var tex = element.objectReferenceValue as Texture2D;
                    EditorGUI.LabelField(nameRect, $"{index + 1}.  {(tex != null ? tex.name : "Empty: an open beam")}");
                    EditorGUI.LabelField(rangeRect, SlotLabel(index + 1), EditorStyles.miniLabel);
                };
                s_lists[wheel] = list;
            }

            using (new EditorGUI.DisabledScope(readOnly))
                list.DoLayoutList();
        }

        /// <summary>
        /// How much video memory the wheel takes, and the one gobo making it larger, if
        /// there is one. Every slot is stored at the largest gobo's size, so one large
        /// gobo grows them all.
        /// </summary>
        internal static void DrawMemory(Texture2D[] gobos)
        {
            if (gobos == null || gobos.Length == 0)
            {
                EditorGUILayout.LabelField("No gobos, so lights project an open beam.", EditorStyles.wordWrappedMiniLabel);
                return;
            }

            int Size(Texture2D t) => t == null ? 0 : Mathf.Max(t.width, t.height);
            int Stored(int size) => Mathf.Clamp(size, VRSLGoboWheel.MinResolution, VRSLGoboWheel.MaxResolution);

            int largest = 0, second = 0;
            Texture2D biggest = null;
            foreach (var g in gobos)
            {
                int s = Size(g);
                if (s > largest) { second = largest; largest = s; biggest = g; }
                else if (s > second) second = s;
            }
            int stored = Stored(largest);
            float mb = (float)stored * stored * 4 * gobos.Length / (1024f * 1024f);
            EditorGUILayout.LabelField(
                $"{gobos.Length} slots, each stored at {stored}×{stored}: {mb:F1} MB of video memory "
              + "while the scene has lights to draw. Frame time is the same however many slots there are.",
                EditorStyles.wordWrappedMiniLabel);

            int without = Stored(second);
            if (biggest != null && stored > without)
            {
                float ratio = (float)stored * stored / ((float)without * without);
                EditorGUILayout.HelpBox(
                    $"{biggest.name} is {biggest.width}×{biggest.height}, larger than the other gobos "
                  + $"({second} at most), so every slot is stored at {stored} and the wheel takes {ratio:F0}× "
                  + "the memory it would otherwise. Make it the size of the others if it doesn't need the detail.",
                    MessageType.Warning);
            }
        }
    }

    /// <summary>
    /// VRSL → URP → Gobo Wheel: the wheel the scene's managers use, a choice of wheel,
    /// its slots, and one button to give every manager in the open scenes that wheel.
    /// </summary>
    class VRSL_GoboWheelWindow : EditorWindow
    {
        VRSLGoboWheelAsset _wheel;
        SerializedObject   _wheelSo;
        Vector2            _scroll;

        [MenuItem("VRSL/URP/Gobo Wheel", false, 150)]
        static void OpenFromMenu() => Open(null);

        /// <summary>Open the window on <paramref name="wheel"/>, or on the wheel the scene
        /// uses when that's null.</summary>
        internal static void Open(VRSLGoboWheelAsset wheel)
        {
            var window = GetWindow<VRSL_GoboWheelWindow>("Gobo Wheel");
            window.Select(wheel != null ? wheel : SceneWheel());
            window.Show();
        }

        static VRSLGoboWheelAsset SceneWheel()
        {
            var used = VRSLGoboWheelGUI.Used(VRSLGoboWheelGUI.SceneManagers(fresh: true));
            return used?.Asset;
        }

        void Select(VRSLGoboWheelAsset wheel)
        {
            _wheel   = wheel;
            _wheelSo = wheel != null ? new SerializedObject(wheel) : null;
        }

        void OnHierarchyChange() => Repaint();
        void OnFocus() => VRSLGoboWheelGUI.SceneManagers(fresh: true);

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawScene();
            EditorGUILayout.Space();
            DrawChoice();
            if (_wheel != null)
            {
                EditorGUILayout.Space();
                DrawWheel();
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawScene()
        {
            EditorGUILayout.LabelField("In the open scenes", EditorStyles.boldLabel);
            var managers = VRSLGoboWheelGUI.SceneManagers();
            if (managers.Count == 0)
            {
                EditorGUILayout.LabelField("No light manager is switched on in the open scenes.",
                                           EditorStyles.wordWrappedMiniLabel);
                return;
            }
            var used = VRSLGoboWheelGUI.Used(managers);
            foreach (var m in managers)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string uses = $"{m.Manager.name} uses {VRSLGoboWheelGUI.Source(m)}"
                                + $" ({m.Gobos?.Length ?? 0} gobos)";
                    EditorGUILayout.LabelField(uses, EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button("Select", GUILayout.Width(60))) Selection.activeObject = m.Manager;
                }
                string conflict = VRSLGoboWheelGUI.Conflict(m.Manager);
                if (conflict != null && used?.Manager != m.Manager)
                    EditorGUILayout.HelpBox(conflict, MessageType.Warning);
            }
        }

        void DrawChoice()
        {
            EditorGUILayout.LabelField("Wheel", EditorStyles.boldLabel);
            var picked = (VRSLGoboWheelAsset)EditorGUILayout.ObjectField(
                "Gobo wheel", _wheel, typeof(VRSLGoboWheelAsset), false);
            if (picked != _wheel) Select(picked);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Make an editable copy of the default…",
                        "A copy of the package's wheel in your project: upstream VRSL's eight gobos and the "
                      + "Club Orion logo, to add your own to.")))
                    CopyDefault();
                if (GUILayout.Button(new GUIContent("New wheel…",
                        "A wheel of your own, starting from upstream VRSL's eight gobos so DMX shows look as "
                      + "they always have.")))
                    CreateNew(null, "VRSL-GoboWheel");
            }

            foreach (var m in VRSLGoboWheelGUI.SceneManagers())
                if (m.Asset == null && m.Gobos != null && m.Gobos.Length > 0
                    && GUILayout.Button($"Make a wheel from {m.Manager.name}'s own gobos…"))
                    CreateNew(m.Gobos, $"VRSL-GoboWheel-{m.Manager.name}");

            using (new EditorGUI.DisabledScope(_wheel == null))
                if (GUILayout.Button(new GUIContent("Use this wheel for every manager in the open scenes",
                        "Links this wheel from every light manager in the open scenes. Undo puts them back.")))
                    AssignToAll(_wheel);
        }

        void DrawWheel()
        {
            _wheelSo.Update();
            bool readOnly = VRSLGoboWheelUpgrade.IsDefault(_wheel);
            if (readOnly)
                EditorGUILayout.HelpBox("This is the package's own wheel, which updates with the package, "
                                      + "so it can't be edited here. Make an editable copy to change it.",
                                        MessageType.Info);
            VRSLGoboWheelGUI.DrawSlots(_wheelSo, readOnly);
            if (!readOnly) _wheelSo.ApplyModifiedProperties();
            VRSLGoboWheelGUI.DrawMemory(_wheel.gobos);
        }

        void CopyDefault()
        {
            var source = VRSLGoboWheelUpgrade.LoadDefault();
            if (source == null)
            {
                EditorUtility.DisplayDialog("Gobo Wheel", "The package's default gobo wheel can't be found.", "OK");
                return;
            }
            string path = EditorUtility.SaveFilePanelInProject("Copy the default gobo wheel",
                "VRSL-GoboWheel", "asset", "Where to keep your copy of the default gobo wheel.");
            if (string.IsNullOrEmpty(path)) return;
            if (!AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(source), path)) return;
            Select(AssetDatabase.LoadAssetAtPath<VRSLGoboWheelAsset>(path));
            EditorGUIUtility.PingObject(_wheel);
        }

        void CreateNew(Texture2D[] gobos, string name)
        {
            string path = EditorUtility.SaveFilePanelInProject("New gobo wheel", name, "asset",
                "Where to keep the new gobo wheel.");
            if (string.IsNullOrEmpty(path)) return;
            var wheel = CreateInstance<VRSLGoboWheelAsset>();
            if (gobos != null) wheel.gobos = (Texture2D[])gobos.Clone();
            else
            {
                var eight = new Texture2D[VRSLGoboWheelUpgrade.UpstreamEightGuids.Length];
                for (int i = 0; i < eight.Length; i++)
                    eight[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(
                        AssetDatabase.GUIDToAssetPath(VRSLGoboWheelUpgrade.UpstreamEightGuids[i]));
                wheel.gobos = eight;
            }
            AssetDatabase.CreateAsset(wheel, path);
            AssetDatabase.SaveAssets();
            Select(wheel);
            EditorGUIUtility.PingObject(wheel);
        }

        /// <summary>Link <paramref name="wheel"/> from every light manager in the open
        /// scenes, as one undo step.</summary>
        internal static int AssignToAll(VRSLGoboWheelAsset wheel)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Use gobo wheel for every manager");
            int count = 0;
            foreach (var m in VRSLLightPaths.Managers(FindObjectsInactive.Include))
            {
                if (m is not MonoBehaviour behaviour || behaviour == null) continue;
                var so = new SerializedObject(behaviour);
                var field = so.FindProperty(VRSLGoboWheelUpgrade.WheelField);
                if (field == null) continue;
                field.objectReferenceValue = wheel;
                so.ApplyModifiedProperties();
                count++;
            }
            Undo.CollapseUndoOperations(group);
            VRSLGoboWheelGUI.SceneManagers(fresh: true);
            Debug.Log($"[VRSL] {count} light manager(s) now use the {wheel.name} gobo wheel. Save the scene to keep it.");
            return count;
        }
    }

    [CustomEditor(typeof(VRSLGoboWheelAsset))]
    class VRSL_GoboWheelAssetEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var wheel = (VRSLGoboWheelAsset)target;
            bool readOnly = VRSLGoboWheelUpgrade.IsDefault(wheel);
            if (readOnly)
                EditorGUILayout.HelpBox("This is the package's own wheel, which updates with the package. "
                                      + "Make an editable copy in VRSL → URP → Gobo Wheel to change it.",
                                        MessageType.Info);
            VRSLGoboWheelGUI.DrawSlots(serializedObject, readOnly);
            if (!readOnly) serializedObject.ApplyModifiedProperties();
            VRSLGoboWheelGUI.DrawMemory(wheel.gobos);
            if (GUILayout.Button("Open the Gobo Wheel window")) VRSL_GoboWheelWindow.Open(wheel);
        }
    }
}
#endif
