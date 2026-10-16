// Editor-only, and in the runtime assembly beside VRSLMigrationTable for the same reason: the
// migration tool compiles only when the legacy package is installed, and the suite has to
// drive the sibling placement and the field translation in any project.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

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

        // Fields on the modern upstream RealtimeLight components we should NOT copy via name-match
        // because their values are Transform/Renderer references that point inside the source
        // prefab's hierarchy — copying them would have the new URP instance referencing upstream
        // children instead of its own. The URP prefab's own internal wiring is left intact.
        static readonly HashSet<string> INTERNAL_REF_FIELDS = new HashSet<string>
        {
            "panTransform", "tiltTransform", "lensTransform", "fixtureShellRenderers", "objRenderers",
        };

        // ── Insertion ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Places <paramref name="urpAsset"/> as the sibling immediately below <paramref name="source"/>
        /// with the same world transform, and carries the source's settings across.
        /// <paramref name="sourceAsset"/> is the upstream prefab the source is an instance of, which
        /// holds upstream's defaults.
        /// </summary>
        public static GameObject PlaceSibling(Transform source, GameObject sourceAsset, GameObject urpAsset,
            VRSLMigrationPair pair, VRSLUpstreamTypes types)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(urpAsset, source.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(instance, "VRSL: Add URP Sibling");

            // Match world transform: parent under same node, copy local scale (since they
            // share a parent the world scale will match), then explicitly write world pos+rot.
            instance.transform.SetParent(source.parent, worldPositionStays: false);
            if (pair.MatchScale) instance.transform.localScale = source.localScale;
            instance.transform.SetPositionAndRotation(source.position, source.rotation);
            instance.transform.SetSiblingIndex(source.GetSiblingIndex() + 1);

            instance.name = source.gameObject.name + " (URP)";

            CopyParameters(source.gameObject, instance, sourceAsset, urpAsset, pair.Kind, types);
            return instance;
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

        static void CopyAudioLinkParameters(GameObject source, GameObject target, GameObject sourceAsset, GameObject targetAsset,
            VRSLUpstreamTypes types)
        {
            // Run only finds AudioLink candidates when the realtime light's type resolves.
            var realtimeType = UrpType(URP_AL_REALTIME);
            if (realtimeType == null) return;
            var dst = target.GetComponentInChildren(realtimeType, includeInactive: true);
            if (dst == null)
            {
                if (!CopyTwinParameters(source, target, types.AudioLinkStatic, UrpType(URP_AL_STATIC)))
                    CopyTwinParameters(source, target, types.AudioLinkLaser, UrpType(URP_AL_LASER));
                return;
            }

            // Prefer modern upstream RealtimeLight if it's also present (effectively identical layout).
            var srcModern = FindComponentByTypeName(source, types.AudioLinkRealtime);
            if (srcModern != null)
            {
                CopyAllNamedFields(srcModern, dst, source);
                EditorUtility.SetDirty(dst);
                return;
            }

            var srcStatic = FindComponentByTypeName(source, types.AudioLinkStatic);
            if (srcStatic == null) return;

            // Direct copies (same name, compatible type — enums round-trip via int)
            CopyField(srcStatic, dst, "enableAudioLink");
            CopyField(srcStatic, dst, "band");
            CopyField(srcStatic, dst, "delay");
            CopyField(srcStatic, dst, "bandMultiplier");
            CopyField(srcStatic, dst, "globalIntensity");
            CopyField(srcStatic, dst, "finalIntensity");
            CopyField(srcStatic, dst, "textureSamplingCoordinates");
            CopyField(srcStatic, dst, "targetToFollow");

            // Renames
            var urpDefault = ReadField(targetAsset.GetComponentInChildren(realtimeType, true),
                "emissionColor", Color.white);
            WriteField(dst, "emissionColor", ScaledTint(srcStatic, sourceAsset, types.AudioLinkStatic, urpDefault));
            CopyField(srcStatic, dst, srcName: "selectGOBO",     dstName: "goboIndex");

            // The light's own conversion, so a migrated scene and a converted clip agree.
            // Pars and blinders keep their angle: upstream's width never reached their shaders.
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

            EditorUtility.SetDirty(dst);
        }

        static void CopyDmxParameters(GameObject source, GameObject target, GameObject sourceAsset, GameObject targetAsset,
            VRSLUpstreamTypes types)
        {
            var dst = target.GetComponentInChildren<VRSLDMXRealtimeLight>(includeInactive: true);
            if (dst == null)
            {
                CopyTwinParameters(source, target, types.DmxStatic, typeof(VRSLDMXStatic));
                return;
            }

            var srcModern = FindComponentByTypeName(source, types.DmxRealtime);
            if (srcModern != null)
            {
                CopyAllNamedFields(srcModern, dst, source);
                EditorUtility.SetDirty(dst);
                return;
            }

            var srcStatic = FindComponentByTypeName(source, types.DmxStatic);
            if (srcStatic == null) return;

            CopyField(srcStatic, dst, "enableDMXChannels");
            CopyField(srcStatic, dst, "enableFineChannels");
            CopyField(srcStatic, dst, "dmxChannel");
            CopyField(srcStatic, dst, "dmxUniverse");
            CopyField(srcStatic, dst, "useLegacySectorMode");
            CopyField(srcStatic, dst, "sector");
            CopyField(srcStatic, dst, "globalIntensity");
            CopyField(srcStatic, dst, "finalIntensity");
            CopyField(srcStatic, dst, "invertPan");
            CopyField(srcStatic, dst, "invertTilt");
            CopyField(srcStatic, dst, "enableStrobe");
            CopyField(srcStatic, dst, "maxMinPan");
            CopyField(srcStatic, dst, "maxMinTilt");

            dst.shellEmissionTint = ScaledTint(srcStatic, sourceAsset, types.DmxStatic,
                targetAsset.GetComponentInChildren<VRSLDMXRealtimeLight>(true).shellEmissionTint);
            CopyField(srcStatic, dst, srcName: "tiltOffsetBlue",     dstName: "tiltOffset");
            CopyField(srcStatic, dst, srcName: "panOffsetBlueGreen", dstName: "panOffset");
            CopyField(srcStatic, dst, srcName: "enableAutoSpin",     dstName: "enableGoboSpin");

            EditorUtility.SetDirty(dst);
        }

        // A URP prefab with no RealtimeLight is a shader-driven fixture (laser, discoball,
        // flasher, light bar) carrying the same component type as upstream. Every same-named
        // field copies across; references into the source's own hierarchy are skipped so the URP
        // prefab keeps its own renderers.
        static bool CopyTwinParameters(GameObject source, GameObject target, string upstreamTypeName, Type urpType)
        {
            if (urpType == null) return false;
            var src = FindComponentByTypeName(source, upstreamTypeName);
            if (src == null) return false;
            var dst = target.GetComponentInChildren(urpType, includeInactive: true);
            if (dst == null) return false;
            CopyAllNamedFields(src, dst, source);
            EditorUtility.SetDirty(dst);
            return true;
        }

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
        public static void BeginRun() => s_WarnedFields.Clear();

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

        // For modern-RT-light → URP-RT-light migrations: copy every same-named field except
        // those whose values point to objects inside the source prefab (panTransform etc.).
        // Preserves the URP prefab's own internal wiring while migrating user-set scalars,
        // colours, enums, and external Transform references like targetToFollow.
        static void CopyAllNamedFields(object src, object dst, GameObject sourceRoot)
        {
            const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var df in dst.GetType().GetFields(FLAGS))
            {
                if (df.IsNotSerialized) continue;
                if (INTERNAL_REF_FIELDS.Contains(df.Name)) continue;

                var sf = src.GetType().GetField(df.Name, FLAGS);
                if (sf == null) continue;

                object value = sf.GetValue(src);

                // Belt-and-braces: if a Transform/Component/GameObject reference happens to point
                // inside the source's hierarchy, skip it — the URP prefab's own equivalent should
                // win. External references (e.g. a stage-mark Transform set by the author) carry over.
                if (value is UnityEngine.Object uo && uo != null && IsInsideSourceHierarchy(uo, sourceRoot))
                    continue;

                CopyField(src, dst, df.Name);
            }
        }

        // In-place twin copy: every serialized field by name, INCLUDING references into the
        // fixture's own hierarchy (objRenderers, transforms). Unlike CopyAllNamedFields nothing is
        // filtered — the URP twin lives on the same GameObject, so those references stay valid.
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
