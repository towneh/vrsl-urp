// Editor-only, and in the runtime assembly beside VRSLMigrationTable for the same reason: the
// migration tool compiles only when the legacy package is installed, and the suite has to
// drive the sibling placement and the field translation in any project.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace VRSL.URP
{
    /// <summary>The full names of the upstream fixture components the translation reads.
    /// The tool passes upstream's own; a test passes stand-ins with upstream's field names.</summary>
    readonly struct VRSLUpstreamTypes
    {
        public readonly string AudioLinkStatic;
        public readonly string AudioLinkRealtime;
        public readonly string AudioLinkLaser;
        public readonly string DmxStatic;
        public readonly string DmxRealtime;

        public VRSLUpstreamTypes(string audioLinkStatic, string audioLinkRealtime, string audioLinkLaser,
            string dmxStatic, string dmxRealtime)
        {
            AudioLinkStatic = audioLinkStatic;
            AudioLinkRealtime = audioLinkRealtime;
            AudioLinkLaser = audioLinkLaser;
            DmxStatic = dmxStatic;
            DmxRealtime = dmxRealtime;
        }

        /// <summary>Upstream com.acchosen.vr-stage-lighting's component names.</summary>
        public static readonly VRSLUpstreamTypes Upstream = new VRSLUpstreamTypes(
            "VRSL.VRStageLighting_AudioLink_Static",
            "VRSL.VRStageLighting_AudioLink_RealtimeLight",
            "VRSL.VRStageLighting_AudioLink_Laser",
            "VRSL.VRStageLighting_DMX_Static",
            "VRSL.VRStageLighting_DMX_RealtimeLight");
    }

    /// <summary>
    /// The migration tool's sibling placement and field translation: puts the URP prefab beside
    /// an upstream fixture and carries its settings across by reflection, so this assembly never
    /// links the upstream package.
    /// </summary>
    static class VRSLMigrationCopy
    {
        // This package's AudioLink components live in an assembly that compiles only where
        // AudioLink is installed, so they are looked up by name too; UrpType answers null
        // without AudioLink and the AudioLink fixtures are left for a later run.
        public const string URP_DMX_STATIC   = "VRSL.URP.VRSLDMXStatic";
        public const string URP_AL_STATIC    = "VRSL.URP.VRSLAudioLinkStatic";
        public const string URP_AL_REALTIME  = "VRSL.URP.VRSLAudioLinkRealtimeLight";
        public const string URP_AL_LASER     = "VRSL.URP.VRSLAudioLinkLaser";

        public static Type UrpType(string fullName)
        {
            foreach (var t in TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
                if (t.FullName == fullName) return t;
            return null;
        }

        // Fields on upstream's fixture components that hold references inside the source prefab's
        // own hierarchy. Copied by name they would point the URP fixture at the original's
        // children, so they're skipped and the URP prefab keeps its own wiring.
        static readonly HashSet<string> INTERNAL_REF_FIELDS = new HashSet<string>
        {
            "panTransform", "tiltTransform", "lensTransform", "fixtureShellRenderers", "objRenderers",
        };

        // ── Insertion ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Places <paramref name="urpAsset"/> as the sibling immediately below <paramref name="source"/>,
        /// its body where the source's is, and carries the source's settings across.
        /// <paramref name="sourceAsset"/> is the upstream prefab the source is an instance of, which
        /// holds upstream's defaults.
        /// </summary>
        public static GameObject PlaceSibling(Transform source, GameObject sourceAsset, GameObject urpAsset,
            VRSLMigrationPair pair, VRSLUpstreamTypes types)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(urpAsset, source.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(instance, "VRSL: Add URP Sibling");

            // Same parent, so the source's local scale gives the same world scale.
            var turn = Quaternion.Euler(pair.RootTurn);
            instance.transform.SetParent(source.parent, worldPositionStays: false);
            if (pair.MatchScale) instance.transform.localScale = TurnedScale(source.localScale, turn);
            instance.transform.SetPositionAndRotation(SiblingPosition(source, pair), source.rotation * turn);
            instance.transform.SetSiblingIndex(source.GetSiblingIndex() + 1);

            instance.name = source.gameObject.name + " (URP)";

            CopyParameters(source.gameObject, instance, sourceAsset, urpAsset, pair.Kind, types);
            MatchState(source.gameObject, instance, types);
            return instance;
        }

        /// <summary>Where <see cref="PlaceSibling"/> puts the sibling's root for this source.</summary>
        public static Vector3 SiblingPosition(Transform source, VRSLMigrationPair pair) =>
            source.TransformPoint(pair.RootShift);

        /// <summary>
        /// Whether a sibling of the pair's URP prefab stands where the tool placed one for this
        /// source: where <see cref="PlaceSibling"/> puts it, or on the source's own root. A re-run
        /// skips such a source.
        /// </summary>
        public static bool IsPlacedFor(Transform sibling, Transform source, VRSLMigrationPair pair, float tolerance)
        {
            float sq = tolerance * tolerance;
            return (sibling.position - SiblingPosition(source, pair)).sqrMagnitude <= sq
                || (sibling.position - source.position).sqrMagnitude <= sq;
        }

        // A quarter-turn maps each of the sibling's axes onto one of the source's; each takes the
        // scale of the axis it lands on, so a stretched original stays stretched along its body.
        static Vector3 TurnedScale(Vector3 scale, Quaternion turn)
        {
            var turned = Vector3.zero;
            for (int i = 0; i < 3; i++)
            {
                var axis = Vector3.zero;
                axis[i] = 1f;
                axis = turn * axis;
                int onto = Mathf.Abs(axis.x) > 0.5f ? 0 : Mathf.Abs(axis.y) > 0.5f ? 1 : 2;
                turned[i] = scale[onto];
            }
            return turned;
        }

        // The sibling stands as its original does: on its layer and tag, its fixture component
        // off when the original's is, and switched off with it, so a fixture the world kept dark
        // stays dark. The prefab's objects on another layer than its root keep theirs.
        static void MatchState(GameObject source, GameObject sibling, VRSLUpstreamTypes types)
        {
            int prefabLayer = sibling.layer;
            foreach (var t in sibling.GetComponentsInChildren<Transform>(includeInactive: true))
                if (t.gameObject.layer == prefabLayer) t.gameObject.layer = source.layer;
            if (Array.IndexOf(InternalEditorUtility.tags, source.tag) >= 0) sibling.tag = source.tag;

            var from = OriginalFixture(source, types);
            var to = SiblingFixture(sibling);
            if (from is Behaviour original && to is Behaviour light) light.enabled = original.enabled;

            sibling.SetActive(source.activeSelf);
        }

        static Component FixtureComponent(GameObject root, params string[] fullNames)
        {
            foreach (var name in fullNames)
            {
                var c = FindComponentByTypeName(root, name);
                if (c != null) return c;
            }
            return null;
        }

        // ── Field translation ─────────────────────────────────────────────────────

        static void CopyParameters(GameObject source, GameObject target, GameObject sourceAsset, GameObject targetAsset,
            VRSLMigrationKind kind, VRSLUpstreamTypes types)
        {
            switch (kind)
            {
                case VRSLMigrationKind.AudioLink: CopyAudioLinkParameters(source, target, sourceAsset, targetAsset, types); break;
                case VRSLMigrationKind.Dmx:       CopyDmxParameters(source, target, sourceAsset, targetAsset, types);       break;
            }
        }

        // The upstream static's fields each realtime path reads. The rest are named when an author
        // changed them, since the realtime light has nowhere to keep them.
        static readonly string[] AL_STATIC_READ =
        {
            "enableAudioLink", "band", "delay", "bandMultiplier", "globalIntensity", "finalIntensity",
            "textureSamplingCoordinates", "targetToFollow", "lightColorTint", "selectGOBO", "coneWidth",
            "enableAutoSpin", "spinSpeed", "enableThemeColorSampling", "enableColorChord",
            "enableColorTextureSampling", "traditionalColorTextureSampling", "themeColorTarget",
        };

        static readonly string[] DMX_STATIC_READ =
        {
            "enableDMXChannels", "enableFineChannels", "dmxChannel", "dmxUniverse", "useLegacySectorMode", "sector",
            "globalIntensity", "finalIntensity", "invertPan", "invertTilt", "enableStrobe", "maxMinPan", "maxMinTilt",
            "lightColorTint", "tiltOffsetBlue", "panOffsetBlueGreen", "enableAutoSpin",
        };

        static void CopyAudioLinkParameters(GameObject source, GameObject target, GameObject sourceAsset, GameObject targetAsset,
            VRSLUpstreamTypes types)
        {
            // Run only finds AudioLink candidates when the realtime light's type resolves.
            var realtimeType = UrpType(URP_AL_REALTIME);
            if (realtimeType == null) return;
            var dst = target.GetComponentInChildren(realtimeType, includeInactive: true);
            if (dst == null)
            {
                if (!CopyTwinParameters(source, target, sourceAsset, targetAsset, types.AudioLinkStatic, UrpType(URP_AL_STATIC)))
                    CopyTwinParameters(source, target, sourceAsset, targetAsset, types.AudioLinkLaser, UrpType(URP_AL_LASER));
                return;
            }

            var dstDefault = targetAsset.GetComponentInChildren(realtimeType, true);

            // Prefer modern upstream RealtimeLight if it's also present (effectively identical layout).
            var srcModern = FindComponentByTypeName(source, types.AudioLinkRealtime);
            if (srcModern != null)
            {
                var srcModernDefault = Defaults(sourceAsset, types.AudioLinkRealtime);
                var carried = CopyAllNamedFields(srcModern, dst, source, srcModernDefault, dstDefault);
                CopyFollowTarget(srcModern, dst, source);
                ReportNotCarried(source, srcModern, srcModernDefault, carried);
                EditorUtility.SetDirty(dst);
                return;
            }

            var srcStatic = FindComponentByTypeName(source, types.AudioLinkStatic);
            if (srcStatic == null) return;
            var srcDefault = Defaults(sourceAsset, types.AudioLinkStatic);

            // Same name. The calibration values are scaled by the two prefabs' defaults; enums
            // round-trip via int.
            CopyField(srcStatic, dst, "enableAudioLink");
            CopyField(srcStatic, dst, "band");
            CopyField(srcStatic, dst, "delay");
            CopyCalibrated(srcStatic, srcDefault, dst, dstDefault, "bandMultiplier");
            CopyCalibrated(srcStatic, srcDefault, dst, dstDefault, "globalIntensity");
            CopyCalibrated(srcStatic, srcDefault, dst, dstDefault, "finalIntensity");
            CopyField(srcStatic, dst, "textureSamplingCoordinates");
            CopyFollowTarget(srcStatic, dst, source);

            // Renames
            var urpDefault = ReadField(dstDefault, "emissionColor", Color.white);
            WriteField(dst, "emissionColor", ScaledTint(srcStatic, sourceAsset, types.AudioLinkStatic, urpDefault));
            CopyField(srcStatic, dst, srcName: "selectGOBO",     dstName: "goboIndex");

            // The light's own conversion, which a script can call too. Pars and blinders keep
            // their angle: upstream's width never reached their shaders.
            float coneWidth = ReadField(srcStatic, "coneWidth", float.NaN);
            var toSpotAngle = realtimeType.GetMethod("SpotAngleForLegacyConeWidth");
            if (!float.IsNaN(coneWidth) && toSpotAngle != null)
                WriteField(dst, "spotAngle", toSpotAngle.Invoke(dst, new object[] { coneWidth }));

            // Spin: only carry over the value when upstream's enableAutoSpin is true,
            // otherwise URP gets 0 (no spin) — matches upstream's runtime behaviour.
            bool autoSpin = ReadField(srcStatic, "enableAutoSpin", false);
            float spin    = ReadField(srcStatic, "spinSpeed",      0f);
            WriteField(dst, "goboSpinSpeed", autoSpin ? spin : 0f);

            // Color mode: collapse upstream's three boolean flags + themeColorTarget into our enum.
            // Upstream's themeColorTarget is 1..4; our enum values ThemeColor0..3 line up after -1.
            // Precedence (theme > chord > texture > emission) matches upstream's shader sampling order.
            bool theme = ReadField(srcStatic, "enableThemeColorSampling", false);
            bool chord = ReadField(srcStatic, "enableColorChord", false);
            bool tex   = ReadField(srcStatic, "enableColorTextureSampling", false);
            bool trad  = ReadField(srcStatic, "traditionalColorTextureSampling", false);
            int  themeIdx = ReadField(srcStatic, "themeColorTarget", 1);

            // By member name, since the enum lives with the component.
            string mode;
            if (theme)      mode = "ThemeColor" + Mathf.Clamp(themeIdx - 1, 0, 3);
            else if (chord) mode = "ColorChord";
            else if (tex)   mode = trad ? "ColorTextureTraditional" : "ColorTexture";
            else            mode = "Emission";
            var modeField = realtimeType.GetField("colorMode");
            modeField.SetValue(dst, Enum.Parse(modeField.FieldType, mode));

            ReportNotCarried(source, srcStatic, srcDefault, AL_STATIC_READ);
            EditorUtility.SetDirty(dst);
        }

        static void CopyDmxParameters(GameObject source, GameObject target, GameObject sourceAsset, GameObject targetAsset,
            VRSLUpstreamTypes types)
        {
            var dst = target.GetComponentInChildren<VRSLDMXRealtimeLight>(includeInactive: true);
            if (dst == null)
            {
                CopyTwinParameters(source, target, sourceAsset, targetAsset, types.DmxStatic, typeof(VRSLDMXStatic));
                return;
            }

            var dstDefault = targetAsset.GetComponentInChildren<VRSLDMXRealtimeLight>(true);
            var srcModern = FindComponentByTypeName(source, types.DmxRealtime);
            if (srcModern != null)
            {
                var srcModernDefault = Defaults(sourceAsset, types.DmxRealtime);
                var carried = CopyAllNamedFields(srcModern, dst, source, srcModernDefault, dstDefault);
                ReportNotCarried(source, srcModern, srcModernDefault, carried);
                EditorUtility.SetDirty(dst);
                return;
            }

            var srcStatic = FindComponentByTypeName(source, types.DmxStatic);
            if (srcStatic == null) return;
            var srcDefault = Defaults(sourceAsset, types.DmxStatic);

            CopyField(srcStatic, dst, "enableDMXChannels");
            CopyField(srcStatic, dst, "enableFineChannels");
            CopyField(srcStatic, dst, "dmxChannel");
            CopyField(srcStatic, dst, "dmxUniverse");
            CopyField(srcStatic, dst, "useLegacySectorMode");
            CopyField(srcStatic, dst, "sector");
            CopyCalibrated(srcStatic, srcDefault, dst, dstDefault, "globalIntensity");
            CopyCalibrated(srcStatic, srcDefault, dst, dstDefault, "finalIntensity");
            CopyField(srcStatic, dst, "invertPan");
            CopyField(srcStatic, dst, "invertTilt");
            CopyField(srcStatic, dst, "enableStrobe");
            CopyField(srcStatic, dst, "maxMinPan");
            CopyField(srcStatic, dst, "maxMinTilt");

            dst.shellEmissionTint = ScaledTint(srcStatic, sourceAsset, types.DmxStatic, dstDefault.shellEmissionTint);
            CopyField(srcStatic, dst, srcName: "tiltOffsetBlue",     dstName: "tiltOffset");
            CopyField(srcStatic, dst, srcName: "panOffsetBlueGreen", dstName: "panOffset");
            CopyField(srcStatic, dst, srcName: "enableAutoSpin",     dstName: "enableGoboSpin");

            ReportNotCarried(source, srcStatic, srcDefault, DMX_STATIC_READ);
            EditorUtility.SetDirty(dst);
        }

        // A URP prefab with no RealtimeLight is a shader-driven fixture (laser, flasher, light
        // bar, strobe) carrying the same component type as upstream. Every same-named
        // field copies across; references into the source's own hierarchy are skipped so the URP
        // prefab keeps its own renderers.
        static bool CopyTwinParameters(GameObject source, GameObject target, GameObject sourceAsset, GameObject targetAsset,
            string upstreamTypeName, Type urpType)
        {
            if (urpType == null) return false;
            var src = FindComponentByTypeName(source, upstreamTypeName);
            if (src == null) return false;
            var dst = target.GetComponentInChildren(urpType, includeInactive: true);
            if (dst == null) return false;
            var srcDefault = Defaults(sourceAsset, upstreamTypeName);
            var carried = CopyAllNamedFields(src, dst, source, srcDefault,
                targetAsset != null ? targetAsset.GetComponentInChildren(urpType, true) : null);
            ReportNotCarried(source, src, srcDefault, carried);
            EditorUtility.SetDirty(dst);
            return true;
        }

        // ── Settings that didn't come across ──────────────────────────────────────

        // Upstream fields an author changed that the new fixture has nowhere to keep, counted by
        // field over the run for the summary.
        static readonly Dictionary<string, int> s_NotCarried = new Dictionary<string, int>();

        /// <summary>This run's changed settings that didn't come across, by field name.</summary>
        public static IReadOnlyDictionary<string, int> NotCarried => s_NotCarried;

        // Each serialised setting on the original that the copy didn't read and the author moved
        // off the upstream prefab's value, named in one warning per fixture. Only plain values:
        // a reference into the original goes with it, and the inspector's fold state is no setting.
        static void ReportNotCarried(GameObject source, Component src, Component srcDefault, ICollection<string> read)
        {
            if (srcDefault == null) return;
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var changed = new List<string>();
            foreach (var f in src.GetType().GetFields(FLAGS))
            {
                bool serialised = f.IsPublic ? !f.IsNotSerialized : f.IsDefined(typeof(SerializeField), true);
                if (!serialised || read.Contains(f.Name) || f.Name == "foldout" || !IsPlainValue(f.FieldType)) continue;
                object value = f.GetValue(src), upstream = f.GetValue(srcDefault);
                if (Equals(value, upstream)) continue;

                s_NotCarried.TryGetValue(f.Name, out int n);
                s_NotCarried[f.Name] = n + 1;
                changed.Add(FormattableString.Invariant(
                    $"{ObjectNames.NicifyVariableName(f.Name)} ({upstream} on the original prefab, {value} here)"));
            }
            if (changed.Count > 0)
                Debug.LogWarning($"[VRSL Migration] '{source.name}' has settings the URP fixture has no place for, so they "
                               + $"didn't come across: {string.Join(", ", changed)}. Check the URP fixture by hand.", source);
        }

        static bool IsPlainValue(Type t) =>
            t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Color)
            || t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4);

        // ── References ────────────────────────────────────────────────────────────

        /// <summary>Every object reference a scene's components hold, by what it points at.</summary>
        public sealed class ReferenceIndex
        {
            readonly Dictionary<UnityEngine.Object, List<(Component holder, string path)>> _byTarget =
                new Dictionary<UnityEngine.Object, List<(Component, string)>>();

            public ReferenceIndex(Scene scene)
            {
                foreach (var root in scene.GetRootGameObjects())
                foreach (var component in root.GetComponentsInChildren<Component>(includeInactive: true))
                {
                    if (component == null || component is Transform) continue;
                    var property = new SerializedObject(component).GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_GameObject") continue;
                        var target = property.objectReferenceValue;
                        if (target == null) continue;
                        if (!_byTarget.TryGetValue(target, out var list))
                            _byTarget[target] = list = new List<(Component, string)>();
                        list.Add((component, property.propertyPath));
                    }
                }
            }

            /// <summary>The slots that held <paramref name="target"/> when the index was built.</summary>
            public IReadOnlyList<(Component holder, string path)> To(UnityEngine.Object target) =>
                _byTarget.TryGetValue(target, out var list) ? list : (IReadOnlyList<(Component, string)>)Array.Empty<(Component, string)>();
        }

        /// <summary>
        /// Points every reference to <paramref name="from"/> at <paramref name="to"/> where the slot can
        /// hold it: a field whose type takes the new component, or a UnityEvent call the new component
        /// can take (a method of the call's name and arguments, as Unity binds it). Returns the
        /// slots that can't, which are left as they are, and counts the ones it moved in
        /// <paramref name="moved"/>.
        /// </summary>
        public static List<(Component holder, string path)> Repoint(Component from, Component to, ReferenceIndex index,
            out int moved)
        {
            moved = 0;
            var left = new List<(Component, string)>();
            foreach (var (holder, path) in index.To(from))
            {
                if (holder == null || holder == from || holder == to) continue;
                var so = new SerializedObject(holder);
                var slot = so.FindProperty(path);
                if (slot == null || slot.objectReferenceValue != from) continue;

                const string TARGET = "m_Target";
                bool eventCall = path.Contains("m_PersistentCalls.m_Calls") && path.EndsWith("." + TARGET);
                if (eventCall)
                {
                    string call = path.Substring(0, path.Length - TARGET.Length);
                    string method = so.FindProperty(call + "m_MethodName")?.stringValue;
                    var arguments = CallArguments(holder, so, path, call);
                    if (string.IsNullOrEmpty(method) || arguments == null
                        || UnityEventBase.GetValidMethodInfo(to.GetType(), method, arguments) == null)
                    {
                        left.Add((holder, path));
                        continue;
                    }
                    var typeName = so.FindProperty(call + "m_TargetAssemblyTypeName");
                    if (typeName != null) typeName.stringValue = to.GetType().FullName + ", " + to.GetType().Assembly.GetName().Name;
                }

                // Unity refuses a value the field's type can't hold, so read it back.
                slot.objectReferenceValue = to;
                if (slot.objectReferenceValue != to)
                {
                    left.Add((holder, path));
                    continue;
                }
                so.ApplyModifiedProperties();
                moved++;
            }
            return left;
        }

        /// <summary>A holder's field, named as the Inspector shows it.</summary>
        public static string FieldName(string path)
        {
            int dot = path.IndexOf('.');
            return ObjectNames.NicifyVariableName(dot < 0 ? path : path.Substring(0, dot));
        }

        // The argument types a persistent call binds with, from its mode (PersistentListenerMode's
        // values), or from the event's own type when the event supplies them. Null when they can't
        // be told, so the call is handed back rather than moved.
        static Type[] CallArguments(Component holder, SerializedObject so, string path, string call)
        {
            var mode = so.FindProperty(call + "m_Mode");
            if (mode == null) return null;
            switch (mode.intValue)
            {
                case 0:
                    int at = path.IndexOf(".m_PersistentCalls", StringComparison.Ordinal);
                    return at < 0 ? null : EventArguments(holder, path.Substring(0, at));
                case 1: return Type.EmptyTypes;
                case 2:
                    string name = so.FindProperty(call + "m_Arguments.m_ObjectArgumentAssemblyTypeName")?.stringValue;
                    var type = string.IsNullOrEmpty(name) ? null : Type.GetType(name, false);
                    return new[] { type ?? typeof(UnityEngine.Object) };
                case 3: return new[] { typeof(int) };
                case 4: return new[] { typeof(float) };
                case 5: return new[] { typeof(string) };
                case 6: return new[] { typeof(bool) };
                default: return null;
            }
        }

        // A UnityEvent<T…>'s argument types, the event found on the holder by its property path.
        static Type[] EventArguments(Component holder, string eventPath)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            object node = holder;
            var parts = eventPath.Split('.');
            for (int i = 0; i < parts.Length && node != null; i++)
            {
                if (parts[i] == "Array" && i + 1 < parts.Length && parts[i + 1].StartsWith("data["))
                {
                    if (!int.TryParse(parts[++i].Substring(5).TrimEnd(']'), out int index) || index < 0) return null;
                    node = node is System.Collections.IList list && index < list.Count ? list[index] : null;
                    continue;
                }
                FieldInfo field = null;
                for (var type = node.GetType(); type != null && field == null; type = type.BaseType)
                    field = type.GetField(parts[i], FLAGS);
                node = field?.GetValue(node);
            }
            if (!(node is UnityEventBase)) return null;
            for (var type = node.GetType(); type != null; type = type.BaseType)
                if (type.IsGenericType && type.Namespace == "UnityEngine.Events" && type.Name.StartsWith("UnityEvent`"))
                    return type.GetGenericArguments();
            return Type.EmptyTypes;
        }

        /// <summary>Something deleting an original fixture would take with it: a reference into it
        /// from elsewhere, or an object or component the author added under it.</summary>
        public readonly struct Loss
        {
            /// <summary>The component holding the reference, or what the author added.</summary>
            public readonly UnityEngine.Object Thing;
            /// <summary>The holder's property path; null for something added.</summary>
            public readonly string Path;
            /// <summary>What the reference points at; null for something added.</summary>
            public readonly UnityEngine.Object Target;
            /// <summary>Where it belongs on the URP fixture: what the reference should point at, or the
            /// object an added one should move under. Null when nothing there matches.</summary>
            public readonly UnityEngine.Object Counterpart;

            public Loss(UnityEngine.Object thing, string path, UnityEngine.Object target, UnityEngine.Object counterpart)
            {
                Thing = thing;
                Path = path;
                Target = target;
                Counterpart = counterpart;
            }
        }

        /// <summary>
        /// What deleting <paramref name="original"/> would take with it, each with its counterpart on
        /// <paramref name="sibling"/>: references to any object or component of it held from outside
        /// it, and what the author added under it.
        /// </summary>
        public static List<Loss> WhatDeletingTakes(GameObject original, GameObject sibling, Component originalFixture,
            Component siblingFixture, ReferenceIndex index)
        {
            var losses = new List<Loss>();
            foreach (var t in original.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                var targets = new List<UnityEngine.Object> { t.gameObject };
                foreach (var c in t.GetComponents<Component>())
                    if (c != null) targets.Add(c);
                foreach (var target in targets)
                foreach (var (holder, path) in index.To(target))
                {
                    if (holder == null || holder.transform.IsChildOf(original.transform)) continue;
                    losses.Add(new Loss(holder, path, target,
                        Counterpart(target, original, sibling, originalFixture, siblingFixture)));
                }
            }

            if (PrefabUtility.IsAnyPrefabInstanceRoot(original))
            {
                foreach (var added in PrefabUtility.GetAddedGameObjects(original))
                {
                    var go = added.instanceGameObject;
                    var parent = go.transform.parent;
                    losses.Add(new Loss(go, null, null, parent != null ? MapTransform(parent, original, sibling) : null));
                }
                foreach (var added in PrefabUtility.GetAddedComponents(original))
                {
                    var c = added.instanceComponent;
                    losses.Add(new Loss(c, null, null, MapTransform(c.transform, original, sibling)));
                }
            }
            return losses;
        }

        // The sibling's counterpart of something in the original: the fixture component for the
        // fixture component, the root for the root, a child of the same name when there's exactly
        // one, and its component of the same type. A renderer on the root with no match there
        // maps to the sibling's only renderer of that type.
        static UnityEngine.Object Counterpart(UnityEngine.Object target, GameObject original, GameObject sibling,
            Component originalFixture, Component siblingFixture)
        {
            if (originalFixture != null && target == originalFixture) return siblingFixture;
            var t = target is GameObject g ? g.transform : ((Component)target).transform;
            var mapped = MapTransform(t, original, sibling);
            if (target is GameObject) return mapped != null ? mapped.gameObject : null;
            if (target is Transform) return mapped;

            var type = target.GetType();
            var match = mapped != null ? mapped.GetComponent(type) : null;
            if (match != null) return match;
            if (t == original.transform && target is Renderer)
            {
                var all = sibling.GetComponentsInChildren(type, includeInactive: true);
                if (all.Length == 1) return all[0];
            }
            return null;
        }

        static Transform MapTransform(Transform t, GameObject original, GameObject sibling)
        {
            if (t == original.transform) return sibling.transform;
            Transform found = null;
            foreach (var candidate in sibling.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                if (candidate == sibling.transform || candidate.name != t.name) continue;
                if (found != null) return null;
                found = candidate;
            }
            return found;
        }

        /// <summary>The fixture component the copy reads on an original, by the upstream names.</summary>
        public static Component OriginalFixture(GameObject original, VRSLUpstreamTypes types) =>
            FixtureComponent(original, types.AudioLinkRealtime, types.AudioLinkStatic, types.AudioLinkLaser,
                types.DmxRealtime, types.DmxStatic);

        /// <summary>A placed sibling's fixture component.</summary>
        public static Component SiblingFixture(GameObject sibling) =>
            FixtureComponent(sibling, URP_AL_REALTIME, URP_AL_STATIC, URP_AL_LASER,
                typeof(VRSLDMXRealtimeLight).FullName, URP_DMX_STATIC);

        // ── Reflection helpers ────────────────────────────────────────────────────

        public static Component FindComponentByTypeName(GameObject root, string fullName)
        {
            foreach (var c in root.GetComponentsInChildren<Component>(includeInactive: true))
            {
                if (c == null) continue;
                if (c.GetType().FullName == fullName) return c;
            }
            return null;
        }

        // Same-name field copy. For enums across different namespaces (e.g. VRSL.AudioLinkBandState
        // vs VRSL.URP.AudioLinkBandState) we round-trip via int since the enum members and ordinals
        // line up by design.
        static void CopyField(object src, object dst, string name) => CopyField(src, dst, name, name);

        static void CopyField(object src, object dst, string srcName, string dstName)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var srcField = src.GetType().GetField(srcName, FLAGS);
            var dstField = dst.GetType().GetField(dstName, FLAGS);
            if (srcField == null || dstField == null) return;

            object value = srcField.GetValue(src);
            var srcType = srcField.FieldType;
            var dstType = dstField.FieldType;

            if (value == null)
            {
                if (!dstType.IsValueType) dstField.SetValue(dst, null);
                return;
            }

            if (dstType.IsAssignableFrom(srcType))
            {
                dstField.SetValue(dst, value);
                return;
            }

            // Older upstream releases store some enums as plain ints (v2.3.0's `band`).
            if (dstType.IsEnum && (srcType.IsEnum || IsIntegral(srcType)))
            {
                int intVal = Convert.ToInt32(value);
                if (Enum.IsDefined(dstType, intVal))
                    dstField.SetValue(dst, Enum.ToObject(dstType, intVal));
                else
                    WarnNotCarried(dst, dstName, $"the old value {intVal} has no match");
                return;
            }

            try
            {
                dstField.SetValue(dst, Convert.ChangeType(value, dstType));
            }
            catch
            {
                WarnNotCarried(dst, dstName, $"the old fixture stores it as {srcType.Name} and the new one as {dstType.Name}");
            }
        }

        static bool IsIntegral(Type t) =>
            t == typeof(int) || t == typeof(short) || t == typeof(byte) || t == typeof(long)
            || t == typeof(uint) || t == typeof(ushort) || t == typeof(sbyte) || t == typeof(ulong);

        // One warning per field per run, so a value that silently stays at the new fixture's
        // default shows up in the Console instead of in the headset.
        static readonly HashSet<string> s_WarnedFields = new HashSet<string>();

        /// <summary>Starts a run: each field not carried is warned about once again.</summary>
        public static void BeginRun()
        {
            s_WarnedFields.Clear();
            s_NotCarried.Clear();
        }

        static void WarnNotCarried(object dst, string field, string reason)
        {
            if (s_WarnedFields.Add(dst.GetType().Name + "." + field))
                Debug.LogWarning($"[VRSL Migration] '{field}' on {dst.GetType().Name} was not carried over ({reason}), " +
                                 "so migrated fixtures keep the new prefab's value. Check it by hand.");
        }

        // The old tint scaled by the two prefabs' defaults, so a fixture left at upstream's
        // default arrives at ours and glows like a freshly placed one. Upstream's defaults are
        // brighter than this package's (2 against 1 on most fixtures).
        static Color ScaledTint(Component srcStatic, GameObject sourceAsset, string upstreamTypeName, Color urpDefault)
        {
            var tint = ReadField(srcStatic, "lightColorTint", urpDefault);
            var srcDefaultComponent = sourceAsset != null ? FindComponentByTypeName(sourceAsset, upstreamTypeName) : null;
            if (srcDefaultComponent == null) return tint;

            float upstreamLevel = ReadField(srcDefaultComponent, "lightColorTint", urpDefault).maxColorComponent;
            if (upstreamLevel <= 0f) return tint;

            float k = urpDefault.maxColorComponent / upstreamLevel;
            return new Color(tint.r * k, tint.g * k, tint.b * k, tint.a);
        }

        // Values this package tuned for its own lighting. They arrive scaled by the two prefabs'
        // defaults, as the tint does, so a fixture left at upstream's default lands on ours and a
        // changed one keeps its share. Every other field carries as the original had it: an
        // address, an inverted pan or AudioLink switched off is what the show was built on.
        static readonly HashSet<string> CALIBRATION_FIELDS = new HashSet<string>
        {
            "globalIntensity", "finalIntensity", "bandMultiplier",
        };

        static void CopyCalibrated(object src, object srcDefault, object dst, object dstDefault, string name)
        {
            float value    = ReadField(src, name, float.NaN);
            float upstream = srcDefault != null ? ReadField(srcDefault, name, float.NaN) : float.NaN;
            float ours     = dstDefault != null ? ReadField(dstDefault, name, float.NaN) : float.NaN;
            if (float.IsNaN(value) || float.IsNaN(upstream) || float.IsNaN(ours) || upstream == 0f)
                CopyField(src, dst, name);
            else
                WriteField(dst, name, value * ours / upstream);
        }

        // The upstream prefab's component of a type, which holds upstream's defaults.
        static Component Defaults(GameObject sourceAsset, string upstreamTypeName) =>
            sourceAsset != null ? FindComponentByTypeName(sourceAsset, upstreamTypeName) : null;

        // Every same-named field except references into the source prefab (panTransform and the
        // like), so the URP prefab keeps its own wiring and gets the author's values and outside
        // references such as targetToFollow.
        static HashSet<string> CopyAllNamedFields(object src, object dst, GameObject sourceRoot, object srcDefault, object dstDefault)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var carried = new HashSet<string>(INTERNAL_REF_FIELDS);
            foreach (var df in dst.GetType().GetFields(FLAGS))
            {
                if (df.IsNotSerialized) continue;
                if (INTERNAL_REF_FIELDS.Contains(df.Name)) continue;

                var sf = src.GetType().GetField(df.Name, FLAGS);
                if (sf == null) continue;
                carried.Add(df.Name);

                object value = sf.GetValue(src);

                // Belt-and-braces: if a Transform/Component/GameObject reference happens to point
                // inside the source's hierarchy, skip it — the URP prefab's own equivalent should
                // win. External references (e.g. a stage-mark Transform set by the author) carry over.
                if (value is UnityEngine.Object uo && uo != null && IsInsideSourceHierarchy(uo, sourceRoot))
                    continue;

                if (CALIBRATION_FIELDS.Contains(df.Name)) CopyCalibrated(src, srcDefault, dst, dstDefault, df.Name);
                else CopyField(src, dst, df.Name);
            }
            return carried;
        }

        // In-place twin copy: every serialised field by name, references into the fixture's own
        // hierarchy (objRenderers, transforms) included. The URP twin lives on the same GameObject,
        // so those references stay valid.
        // Arrays are cloned so the two components don't share one backing array before the upstream
        // component is removed.
        public static void CopyAllFieldsByName(Component src, Component dst)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var df in dst.GetType().GetFields(FLAGS))
            {
                if (df.IsNotSerialized) continue;
                var sf = src.GetType().GetField(df.Name, FLAGS);
                if (sf == null) continue;

                if (df.FieldType.IsAssignableFrom(sf.FieldType))
                {
                    object v = sf.GetValue(src);
                    if (v is Array arr) v = arr.Clone();
                    df.SetValue(dst, v);
                }
                else
                {
                    CopyField(src, dst, df.Name);   // enum / Convert fallback for differing types
                }
            }
            EditorUtility.SetDirty(dst);
        }

        // An AudioLink mover aims at its own PanTiltTarget, upstream's and ours. A target inside the
        // original stays ours, moved to where the original's stands, so the aim survives deleting the
        // original; a target outside it carries across as it is.
        static void CopyFollowTarget(Component src, Component dst, GameObject sourceRoot)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var srcField = src.GetType().GetField("targetToFollow", FLAGS);
            var dstField = dst.GetType().GetField("targetToFollow", FLAGS);
            if (srcField == null || dstField == null) return;

            var target = srcField.GetValue(src) as Transform;
            if (target == null || !IsInsideSourceHierarchy(target, sourceRoot))
            {
                dstField.SetValue(dst, target);
                return;
            }
            var own = dstField.GetValue(dst) as Transform;
            if (own == null) return;
            own.position = target.position;
            PrefabUtility.RecordPrefabInstancePropertyModifications(own);
        }

        static bool IsInsideSourceHierarchy(UnityEngine.Object value, GameObject sourceRoot)
        {
            if (sourceRoot == null) return false;
            Transform t = value switch
            {
                GameObject g => g.transform,
                Component c  => c.transform,
                _            => null,
            };
            return t != null && t.IsChildOf(sourceRoot.transform);
        }

        static T ReadField<T>(object src, string name, T fallback)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var f = src.GetType().GetField(name, FLAGS);
            if (f == null) return fallback;
            object v = f.GetValue(src);
            if (v == null) return fallback;
            try { return (T)Convert.ChangeType(v, typeof(T)); }
            catch { return fallback; }
        }

        static void WriteField(object dst, string name, object value)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var f = dst.GetType().GetField(name, FLAGS);
            if (f == null) return;
            f.SetValue(dst, value);
        }
    }
}
#endif
