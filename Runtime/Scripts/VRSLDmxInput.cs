using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// Where the DMX manager takes DMX from: one of the three video grid layouts. The
    /// values match <see cref="VRSLGridLayout"/>'s.
    /// </summary>
    public enum VRSLDmxInput
    {
        [InspectorName("Horizontal grid")] HorizontalGrid = 0,
        [InspectorName("Vertical grid")]   VerticalGrid   = 1,
        [InspectorName("Legacy grid")]     LegacyGrid     = 2,
    }
}
