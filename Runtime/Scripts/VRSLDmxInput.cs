using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// Where the DMX manager takes DMX from: one of the three video grid layouts, or the
    /// channel source (Truss records from the stream, OSC, or any other
    /// <see cref="IVRSLDMXChannelSource"/>). The grid values match
    /// <see cref="VRSLGridLayout"/>'s.
    /// </summary>
    public enum VRSLDmxInput
    {
        [InspectorName("Horizontal grid")] HorizontalGrid = 0,
        [InspectorName("Vertical grid")]   VerticalGrid   = 1,
        [InspectorName("Legacy grid")]     LegacyGrid     = 2,
        [InspectorName("Stream data")]     StreamData     = 3,
    }
}
