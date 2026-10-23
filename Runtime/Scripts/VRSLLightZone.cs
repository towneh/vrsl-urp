using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// A space VRSL lights can be kept inside. The zone is the union of the
    /// <see cref="VRSLLightZoneBox"/>es on this GameObject and its children; a realtime
    /// light assigned to it lights only what is inside them, in surface light, beams, the
    /// disco ball's dots and gobos. Its lens flare and body glow are not limited.
    /// </summary>
    /// <remarks>
    /// A light takes its zone from its own <c>lightZone</c> field, or failing that from the
    /// nearest Light Zone among its parents, so a whole rig is zoned by putting one on the
    /// rig's root. A zone switched off stops limiting its lights; a zone switched on with
    /// every box switched off keeps them dark.
    ///
    /// Nothing inside a zone blocks light: a pillar or a person between a fixture and the
    /// wall behind it still lets the light through to the wall. Put a box's face inside the
    /// wall that should stop the light, not on its surface.
    ///
    /// Moving the zone, switching boxes on and off, moving a box or a light (or a parent
    /// above it) in or out of the zone, editing a box in the inspector and animating its
    /// size are picked up on their own. A box moved on its own while the
    /// world runs (by a script or an animation that doesn't move the zone), or resized by
    /// a script, needs <see cref="Refresh"/> afterwards.
    /// </remarks>
    [AddComponentMenu("VRSL/Light Zone")]
    [DisallowMultipleComponent]
    public class VRSLLightZone : MonoBehaviour
    {
        /// <summary>Where this zone's boxes sit in the zone data, or -1 while it is
        /// switched off.</summary>
        internal int Slot = -1;

        /// <summary>This zone's transform when the box data was last built, so a moved
        /// zone is noticed without polling its boxes.</summary>
        internal Matrix4x4 BuiltAt;

        void OnEnable()  => VRSLLightZones.Join(this);
        void OnDisable() => VRSLLightZones.Leave(this);

        // A box moved in or out of the hierarchy.
        void OnTransformChildrenChanged() => VRSLLightZones.MarkChanged();

        /// <summary>Pick up boxes moved or resized by a script or an animation that didn't
        /// move the zone itself, from the next frame.</summary>
        public void Refresh() => VRSLLightZones.MarkChanged();

#if UNITY_EDITOR
        // A zone does nothing without a box, so a new one starts with one around it.
        void Reset()
        {
            if (GetComponentInChildren<VRSLLightZoneBox>(true) == null)
                gameObject.AddComponent<VRSLLightZoneBox>();
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>The Zone field on both realtime lights' inspectors, with a line saying
    /// which zone the light ends up in when it names none.</summary>
    internal static class VRSLLightZoneField
    {
        public static void Draw(UnityEditor.SerializedProperty property, Transform light)
        {
            UnityEditor.EditorGUILayout.PropertyField(property, new GUIContent("Zone", property.tooltip));
            if (property.hasMultipleDifferentValues || property.objectReferenceValue != null) return;
            var inherited = light.GetComponentInParent<VRSLLightZone>(true);
            UnityEditor.EditorGUILayout.HelpBox(inherited != null
                ? $"Stays inside {inherited.name}, the Light Zone above it."
                : "No zone: lights everything its range reaches, through walls too.",
                UnityEditor.MessageType.None);
        }
    }
#endif
}
