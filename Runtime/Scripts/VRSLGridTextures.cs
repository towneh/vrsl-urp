using System;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// The textures that decode a video's DMX grid, one set per grid layout. The DMX
    /// manager points at one of these and reads the set for its layout.
    /// </summary>
    /// <remarks>
    /// The package ships one holding its own decode chain for all three layouts, and the
    /// manager fills itself in with it. To decode through textures of your own, make
    /// another (Create → VRSL → URP → Grid Textures, or duplicate the shipped one), fill
    /// in the layouts you use and point the manager at it. Edit your copy, never the
    /// package's.
    /// </remarks>
    [CreateAssetMenu(menuName = "VRSL/URP/Grid Textures", fileName = "VRSL-DMX-GridTextures")]
    public class VRSLGridTextures : ScriptableObject
    {
        /// <summary>One layout's decode chain.</summary>
        [Serializable]
        public struct Set
        {
            [Tooltip("The colour and intensity of every channel, decoded from the video. "
                   + "Without it this layout lights nothing.")]
            public RenderTexture main;

            [Tooltip("The channels as they arrive from the video, before smoothing. Shaders "
                   + "that read unsmoothed values read this; left empty, they get the smoothed "
                   + "values above.")]
            public RenderTexture raw;

            [Tooltip("Pan and tilt, smoothed. Without it moving heads stay where they are.")]
            public RenderTexture movement;

            [Tooltip("Whether each fixture's strobe is open this instant. Without it strobe "
                   + "does nothing.")]
            public RenderTexture strobe;

            [Tooltip("The strobe's timing, which the strobe texture above reads. Leave it "
                   + "empty only if nothing in the scene strobes: where strobe is used, a "
                   + "missing one takes every strobing fixture dark.")]
            public RenderTexture strobeTimer;

            [Tooltip("How far each gobo has turned. Without it gobos stop spinning.")]
            public RenderTexture spinTimer;
        }

        public Set horizontal;
        public Set vertical;
        public Set legacy;

        /// <summary>The set for <paramref name="layout"/>.</summary>
        public Set For(VRSLGridLayout layout) => layout switch
        {
            VRSLGridLayout.Vertical => vertical,
            VRSLGridLayout.Legacy   => legacy,
            _                       => horizontal,
        };
    }
}
