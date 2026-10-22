using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// One box of a <see cref="VRSLLightZone"/>, on the zone's GameObject or any child of
    /// it. Oriented and scaled with its transform, so a box can be turned to follow a wall.
    /// </summary>
    [AddComponentMenu("VRSL/Light Zone Box")]
    public class VRSLLightZoneBox : MonoBehaviour
    {
        [Tooltip("The centre of the box, in this object's local space.")]
        public Vector3 center = Vector3.zero;

        [Tooltip("The size of the box, in this object's local space. Put a face inside the wall "
               + "that should stop the light, not on its surface.")]
        public Vector3 size = Vector3.one;

        /// <summary>From this box's space (a unit cube centred on the origin) to the world.</summary>
        internal Matrix4x4 BoxToWorld => transform.localToWorldMatrix * Matrix4x4.TRS(center, Quaternion.identity, size);

        void OnEnable()  => VRSLLightZones.MarkChanged();
        void OnDisable() => VRSLLightZones.MarkChanged();

        // An animation driving the size or centre.
        void OnDidApplyAnimationProperties() => VRSLLightZones.MarkChanged();

#if UNITY_EDITOR
        void OnValidate() => VRSLLightZones.MarkChanged();

        void OnDrawGizmos() => Draw(false);
        void OnDrawGizmosSelected() => Draw(true);

        void Draw(bool selected)
        {
            var zone = GetComponentInParent<VRSLLightZone>();
            bool live = zone != null && zone.isActiveAndEnabled && isActiveAndEnabled;
            var colour = live ? new Color(1f, 0.75f, 0.2f) : new Color(0.6f, 0.6f, 0.6f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color  = colour;
            Gizmos.DrawWireCube(center, size);
            if (selected)
            {
                Gizmos.color = new Color(colour.r, colour.g, colour.b, 0.12f);
                Gizmos.DrawCube(center, size);
            }
            Gizmos.matrix = Matrix4x4.identity;
        }
#endif
    }
}
