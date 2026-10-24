using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRSL.URP
{
    /// <summary>
    /// The scene's light zones, as the shaders read them: one structured buffer of
    /// <c>float4</c>, bound as the global <c>_VRSLZones</c> by this class alone.
    /// </summary>
    /// <remarks>
    /// The buffer opens with one header per zone slot, <c>(first box row, box count, 0,
    /// 0)</c>, followed by three rows per box: the box's world-to-box transform, whose
    /// image of a point inside the box lies in the unit cube centred on the origin. A light
    /// carries its zone as slot + 1 (0 for none) in its packed type (see
    /// <c>VRSL_PackTypeGoboZone</c>).
    ///
    /// Zones join on enable and leave on disable, and keep their slot while enabled, so
    /// one zone switching leaves every other light's packed slot as it was. Either moves
    /// <see cref="Version"/> and marks every DMX light's config dirty, so a light's zone
    /// follows on the next upload. The buffer is rebuilt at most once a frame from
    /// <see cref="Prepare"/>, and only when something changed: a zone came or went, a
    /// zone moved, or a box was switched, edited or animated.
    ///
    /// Bound with <c>Shader.SetGlobalBuffer</c> outside the render graph, so it holds
    /// across every camera and reaches shaders outside VRSL's own passes. With no zone
    /// enabled the fallback buffer is bound instead, since D3D12 and Vulkan skip a draw
    /// whose declared buffer is unbound.
    ///
    /// The global keyword <c>VRSL_LIGHT_ZONES</c> selects the lighting and raymarch
    /// variants that read zones. It goes on when the first zone joins and off a couple of
    /// frames after the last one leaves, once every light's packed type has been rebuilt
    /// without its zone, so a scene with no zone on renders with no zone code at all.
    /// </remarks>
    internal static class VRSLLightZones
    {
        static readonly int s_Zones = Shader.PropertyToID("_VRSLZones");
        static readonly GlobalKeyword s_Keyword = GlobalKeyword.Create("VRSL_LIGHT_ZONES");

        /// <summary>The most zones a scene can enable at once: the slot is packed into a
        /// float above the light type and gobo, which is exact to 2^24.</summary>
        internal const int MaxZones = (1 << 14) - 1;

        static readonly List<VRSLLightZone>    s_slots = new();
        static readonly List<VRSLLightZoneBox> s_boxes = new();
        static Vector4[]      s_data = new Vector4[0];
        static GraphicsBuffer s_buffer;
        static int  s_count;
        static bool s_dirty = true;
        static int  s_preparedFrame = -1;
        static bool s_warnedFull;
        static bool s_keywordOn;
        static int  s_keywordOffFrame = -1;

        /// <summary>Zones enabled now.</summary>
        public static int Count => s_count;

        /// <summary>Moves whenever a zone joins or leaves, so a light that takes its zone
        /// from a parent knows to look again.</summary>
        public static int Version { get; private set; }

        /// <summary>The live zone data, or null with no zone enabled.</summary>
        internal static GraphicsBuffer Buffer => s_buffer;

        /// <summary>Box data uploads and global binds since load, for the row that checks
        /// a frame with nothing changed does neither.</summary>
        internal static int Uploads { get; private set; }
        internal static int Binds   { get; private set; }

        internal static void Join(VRSLLightZone zone)
        {
            if (zone == null || zone.Slot >= 0) return;
            int slot = s_slots.IndexOf(null);
            if (slot < 0)
            {
                if (s_slots.Count >= MaxZones)
                {
                    if (!s_warnedFull)
                        Debug.LogWarning($"[VRSL URP] More than {MaxZones} light zones are switched on at once, "
                                       + $"so {zone.name} doesn't limit its lights. Switch some zones off.", zone);
                    s_warnedFull = true;
                    return;
                }
                slot = s_slots.Count;
                s_slots.Add(zone);
            }
            else s_slots[slot] = zone;
            zone.Slot = slot;
            s_count++;
            SetKeyword(true);
            Changed();
        }

        internal static void Leave(VRSLLightZone zone)
        {
            if (zone == null || zone.Slot < 0) return;
            if (zone.Slot < s_slots.Count && s_slots[zone.Slot] == zone) s_slots[zone.Slot] = null;
            zone.Slot = -1;
            s_count--;
            while (s_slots.Count > 0 && s_slots[s_slots.Count - 1] == null) s_slots.RemoveAt(s_slots.Count - 1);
            if (s_count == 0) Release();
            Changed();
        }

        static void Changed()
        {
            Version++;
            s_dirty = true;
            VRSLDMXRealtimeLight.RaiseConfigChanged();
        }

        /// <summary>Rebuild the box data on the next <see cref="Prepare"/>.</summary>
        internal static void MarkChanged() => s_dirty = true;

        /// <summary>
        /// A light's zone as the light data carries it: the slot of
        /// <paramref name="named"/>, or of the nearest zone above <paramref name="from"/>
        /// when none is named, plus one; 0 for none or a zone switched off.
        /// <paramref name="parent"/> and <paramref name="seen"/> are the light's own cache
        /// of the parent lookup, taken again whenever a zone joins or leaves.
        /// </summary>
        internal static float CodeFor(VRSLLightZone named, Transform from, ref VRSLLightZone parent, ref int seen)
        {
            if (s_count == 0) return 0f;
            var zone = named;
            if (zone == null)
            {
                if (seen != Version)
                {
                    parent = from != null ? from.GetComponentInParent<VRSLLightZone>() : null;
                    seen   = Version;
                }
                zone = parent;
            }
            return zone != null && zone.Slot >= 0 ? zone.Slot + 1 : 0f;
        }

        /// <summary>
        /// Bring the zone data up to date, at most once a frame. Called from the scene
        /// resources' own <c>Prepare</c>, which each manager calls before it records
        /// anything. With no zone enabled it returns at once; with nothing changed it
        /// compares one matrix per zone and uploads nothing.
        /// </summary>
        public static void Prepare()
        {
            if (s_count == 0)
            {
                if (s_keywordOffFrame >= 0 && Time.frameCount >= s_keywordOffFrame) SetKeyword(false);
                return;
            }
            int frame = Time.frameCount;
            if (s_preparedFrame == frame) return;
            s_preparedFrame = frame;

            for (int i = 0; i < s_slots.Count; i++)
            {
                var zone = s_slots[i];
                if (zone is null) continue;
                // Destroyed without its OnDisable (a scene unloading under it).
                if (zone == null) { s_slots[i] = null; s_count--; Version++; s_dirty = true; continue; }
                if (zone.transform.localToWorldMatrix != zone.BuiltAt) s_dirty = true;
            }

            if (!s_dirty) return;
            s_dirty = false;
            if (s_count == 0) { Release(); return; }
            Rebuild();
        }

        static void Rebuild()
        {
            int slots = s_slots.Count;
            int rows  = slots;
            for (int i = 0; i < slots; i++)
            {
                var zone = s_slots[i];
                if (zone is null) continue;
                zone.GetComponentsInChildren(false, s_boxes);
                rows += 3 * s_boxes.Count;
            }
            if (s_data.Length < rows) s_data = new Vector4[Mathf.NextPowerOfTwo(rows)];

            int row = slots;
            for (int i = 0; i < slots; i++)
            {
                var zone = s_slots[i];
                if (zone is null) { s_data[i] = Vector4.zero; continue; }
                zone.BuiltAt = zone.transform.localToWorldMatrix;
                zone.GetComponentsInChildren(false, s_boxes);
                int first = row;
                foreach (var box in s_boxes)
                {
                    if (!box.enabled) continue;
                    var boxToWorld = box.BoxToWorld;
                    // A box squashed flat holds nothing, and has no inverse.
                    if (Mathf.Abs(boxToWorld.determinant) < 1e-12f) continue;
                    var worldToBox = boxToWorld.inverse;
                    s_data[row++] = worldToBox.GetRow(0);
                    s_data[row++] = worldToBox.GetRow(1);
                    s_data[row++] = worldToBox.GetRow(2);
                }
                s_data[i] = new Vector4(first, (row - first) / 3, 0f, 0f);
            }
            s_boxes.Clear();

            if (s_buffer == null || !s_buffer.IsValid() || s_buffer.count < row)
            {
                s_buffer?.Release();
                s_buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                                              Mathf.Max(Mathf.NextPowerOfTwo(row), 1), 16);
                Shader.SetGlobalBuffer(s_Zones, s_buffer);
                Binds++;
            }
            s_buffer.SetData(s_data, 0, 0, row);
            Uploads++;
        }

        static void Release()
        {
            s_buffer?.Release();
            s_buffer = null;
            s_dirty  = true;
            s_preparedFrame = -1;
            // Lights still carry their zone until the managers' next config upload, which
            // can be the frame after this one.
            if (s_keywordOn) s_keywordOffFrame = Time.frameCount + 2;
            var fallback = VRSLDMXFallbackBuffers.Zones;
            if (fallback != null)
            {
                Shader.SetGlobalBuffer(s_Zones, fallback);
                Binds++;
            }
        }

        /// <summary>Whether the shaders' zone variants are selected now.</summary>
        internal static bool KeywordOn => s_keywordOn;

        static void SetKeyword(bool on)
        {
            s_keywordOffFrame = -1;
            if (s_keywordOn == on) return;
            s_keywordOn = on;
            Shader.SetKeyword(s_Keyword, on);
        }

#if UNITY_EDITOR
        // An author dragging or resizing a box in the scene view or the inspector in Play.
        // The editor's own edits only, so a script moving a box still needs Refresh() here
        // as it does in a player.
        [UnityEditor.InitializeOnLoadMethod]
        static void WatchEdits()
        {
            UnityEditor.ObjectChangeEvents.changesPublished -= OnEdits;
            UnityEditor.ObjectChangeEvents.changesPublished += OnEdits;
        }

        static void OnEdits(ref UnityEditor.ObjectChangeEventStream stream)
        {
            if (s_count > 0) s_dirty = true;
        }
#endif

        /// <summary>Statics survive entering Play with domain reload off; anything left
        /// from before is let go.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnLoad()
        {
            foreach (var zone in s_slots) if (zone != null) zone.Slot = -1;
            s_slots.Clear();
            s_count = 0;
            s_warnedFull = false;
            s_buffer?.Release();
            s_buffer = null;
            s_dirty = true;
            s_preparedFrame = -1;
            // The keyword is global state that outlives the statics.
            s_keywordOn = false;
            s_keywordOffFrame = -1;
            Shader.SetKeyword(s_Keyword, false);
        }
    }
}
