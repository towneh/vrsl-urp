using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// What the package's own tools need from a light manager, whichever path it drives:
    /// the benchmark, the quality presets, the control panel, renderer validation and the
    /// manager inspector. Most members are ones both managers already have under these
    /// names; the rest forward to a serialised field.
    ///
    /// The DMX manager implements it here. A manager in an optional integration
    /// implements it in its own assembly and registers its <see cref="VRSLLightPath"/>,
    /// so nothing in this assembly names it.
    /// </summary>
    internal interface IVRSLLightManager : IVRSLLightSource
    {
        /// <summary>True when this is the manager its path's render passes run from.
        /// A second manager of the same kind stands down and answers false.</summary>
        bool OwnsSingleton { get; }

        /// <summary>The author's quality setting (the <c>quality</c> field).</summary>
        VRSLQuality QualitySetting { get; set; }

        /// <summary>The costs that setting resolves to.</summary>
        VRSLQualityLevel Quality { get; }

        /// <summary>The level a camera last rendered at, or null when it has not.</summary>
        VRSLQuality? QualityFor(Camera cam);

        bool VolumetricsEnabled { get; }
        VRSLVolumetricStatsProbe VolumetricStats { get; }
        VRSLTileCullPass TileCullPass { get; }
        bool UsesUrpNormals { get; }
        bool UsesUrpNormalsFor(Camera cam);

        float LightIntensity { get; set; }
        float VolumetricIntensity { get; set; }
        SecondaryCameraMode SecondaryCameraMode { get; }
        bool ForceOwnNormals { get; }
        LayerMask PrepassLayers { get; }

        /// <summary>Why every fixture on this path might read dark, and what the author
        /// does about it, for a benchmark that measured an unlit scene.</summary>
        string DarkFixturesReason { get; }
    }

    /// <summary>What the control panel's discoball slider needs from a realtime
    /// light.</summary>
    internal interface IVRSLRealtimeLight
    {
        bool IsDiscoball { get; }
        float GlobalIntensity { get; set; }
    }

    /// <summary>A manager whose fixtures can take their colour from a texture the
    /// control panel hands it.</summary>
    internal interface IVRSLSamplingTextureTarget
    {
        Texture SamplingTexture { set; }
    }

    /// <summary>
    /// One way of driving fixtures: the manager that runs it and the realtime light it
    /// drives. The tools find managers and fixtures by these types.
    /// </summary>
    internal sealed class VRSLLightPath
    {
        /// <summary>The path's name in summaries: "DMX", "AudioLink".</summary>
        public readonly string Name;
        public readonly Type   Manager;
        public readonly Type   RealtimeLight;

        public VRSLLightPath(string name, Type manager, Type realtimeLight)
        {
            Name = name; Manager = manager; RealtimeLight = realtimeLight;
        }
    }

    /// <summary>
    /// Every light path in this project. DMX is always here; an optional integration
    /// registers its own from <c>[RuntimeInitializeOnLoadMethod]</c> and
    /// <c>[InitializeOnLoadMethod]</c>, so play mode, players and the editor all see it.
    /// </summary>
    internal static class VRSLLightPaths
    {
        public static readonly VRSLLightPath Dmx =
            new("DMX", typeof(VRSL_DMXURPLightManager), typeof(VRSLDMXRealtimeLight));

        static readonly List<VRSLLightPath> s_All = new() { Dmx };

        public static IReadOnlyList<VRSLLightPath> All => s_All;

        /// <summary>Add a path. Registering the same manager type again changes
        /// nothing, since both load hooks call this and a domain reload may not
        /// clear it.</summary>
        public static void Register(VRSLLightPath path)
        {
            if (path == null) return;
            foreach (var existing in s_All)
                if (existing.Manager == path.Manager) return;
            s_All.Add(path);
        }

        /// <summary>The path a manager runs, or null for anything else.</summary>
        public static VRSLLightPath Of(object manager)
        {
            if (manager == null) return null;
            var type = manager.GetType();
            foreach (var path in s_All)
                if (path.Manager == type) return path;
            return null;
        }

        /// <summary>Unity's null for something held through an interface: false for a
        /// destroyed component as well as a missing one, which a plain
        /// <c>!= null</c> on the interface does not see.</summary>
        public static bool IsAlive(object o) => o is UnityEngine.Object u ? u != null : o != null;

        /// <summary>The path's name for a manager, for summaries.</summary>
        public static string NameOf(object manager) => Of(manager)?.Name ?? "VRSL";

        /// <summary>Every manager of every path in the open scenes.</summary>
        public static List<IVRSLLightManager> Managers(FindObjectsInactive inactive)
        {
            var found = new List<IVRSLLightManager>();
            foreach (var path in s_All)
                foreach (var o in UnityEngine.Object.FindObjectsByType(path.Manager, inactive))
                    if (o is IVRSLLightManager m) found.Add(m);
            return found;
        }

        /// <summary>One manager for every path but DMX: the one that owns its singleton,
        /// or with <paramref name="ownerOnly"/> false the first one in the scene when
        /// none does yet (the control panel resolves before the claim can have
        /// happened in edit mode).</summary>
        public static List<IVRSLLightManager> OtherManagers(bool ownerOnly)
        {
            var found = new List<IVRSLLightManager>();
            foreach (var path in s_All)
            {
                if (path == Dmx) continue;
                IVRSLLightManager first = null, owner = null;
                foreach (var o in UnityEngine.Object.FindObjectsByType(path.Manager, FindObjectsInactive.Exclude))
                {
                    if (o is not IVRSLLightManager m) continue;
                    first ??= m;
                    if (m.OwnsSingleton) { owner = m; break; }
                }
                var chosen = ownerOnly ? owner : owner ?? first;
                if (chosen != null) found.Add(chosen);
            }
            return found;
        }

        /// <summary>The running manager of the first path other than DMX that has one,
        /// or null. What the benchmark and the sweep measure beside DMX.</summary>
        public static IVRSLLightManager RunningOther()
        {
            var others = OtherManagers(ownerOnly: true);
            return others.Count > 0 ? others[0] : null;
        }

        /// <summary>Every realtime light of every path in the open scenes.</summary>
        public static List<IVRSLRealtimeLight> RealtimeLights(FindObjectsInactive inactive)
        {
            var found = new List<IVRSLRealtimeLight>();
            foreach (var path in s_All)
                foreach (var o in UnityEngine.Object.FindObjectsByType(path.RealtimeLight, inactive))
                    if (o is IVRSLRealtimeLight light) found.Add(light);
            return found;
        }
    }
}
