namespace VRSL.URP
{
    /// <summary>
    /// How a video lays its DMX channels out as a grid of cells: one of VRSL's three
    /// layouts. A lighting desk or encoder sends one of them, and the DMX manager has to
    /// be set to the same one.
    /// </summary>
    public enum VRSLGridLayout
    {
        Horizontal = 0,
        Vertical   = 1,
        /// <summary>VRSL's original layout, from before Horizontal and Vertical.</summary>
        Legacy     = 2,
    }
}
