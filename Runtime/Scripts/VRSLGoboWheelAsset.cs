using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// A gobo wheel: the gobos the scene's moving lights can project, in order. Both light
    /// managers link one, and every light in the scene indexes the same slots.
    /// </summary>
    /// <remarks>
    /// The package ships a default holding upstream VRSL's eight gobos in slots 1 to 8 and
    /// the Club Orion logo in slot 9. DMX lights pick from the first eight by the usual DMX
    /// ranges; AudioLink lights reach every slot through their Gobo Index. To use gobos of
    /// your own, make a wheel (VRSL → URP → Gobo Wheel, or Create → VRSL → URP → Gobo
    /// Wheel) and link it from the managers. Edit your copy, never the package's.
    /// </remarks>
    [CreateAssetMenu(menuName = "VRSL/URP/Gobo Wheel", fileName = "VRSL-GoboWheel")]
    public class VRSLGoboWheelAsset : ScriptableObject
    {
        /// <summary>Slots DMX can choose from. Upstream VRSL's gobo channel picks one of
        /// eight, and shows already programmed keep those ranges.</summary>
        public const int DmxSlots = 8;

        [Tooltip("The gobos, slot 1 first. DMX lights reach the first eight; AudioLink lights "
               + "reach every slot.")]
        public Texture2D[] gobos;

#if UNITY_EDITOR
        /// <summary>Raised when the wheel is edited, so a running scene picks the change
        /// up.</summary>
        internal static event System.Action<VRSLGoboWheelAsset> Edited;

        void OnValidate() => Edited?.Invoke(this);
#endif
    }
}
