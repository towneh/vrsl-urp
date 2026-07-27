using System.Collections.Generic;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>How VRSL treats cameras that render into a texture rather than to
    /// the player's view — mirrors, portals, and camera props.</summary>
    public enum SecondaryCameraMode
    {
        /// <summary>Light them exactly like the main view. Beams in a mirror are a
        /// large part of a stage look, so this is the default.</summary>
        Full = 0,

        /// <summary>Run surface lighting but skip the volumetric raymarch, which is
        /// by far the more expensive of the two.</summary>
        SurfaceOnly = 1,

        /// <summary>No VRSL passes at all. Cheapest, and visibly wrong in a mirror
        /// pointed at the rig.</summary>
        Skip = 2,
    }

    /// <summary>What VRSL should do for a given camera.</summary>
    public enum VRSLCameraDecision
    {
        Skip = 0,
        SurfaceOnly = 1,
        Full = 2,
    }

    /// <summary>
    /// Decides which cameras VRSL injects its passes into.
    ///
    /// The load-bearing case is not performance. The lighting pass blends
    /// <c>One One</c> onto the active colour target, so on a camera whose colour
    /// target is a texture VRSL itself reads back, the additive light corrupts that
    /// data. The DMX screen reader is exactly that: an orthographic camera whose
    /// target texture is the RAW-values RT feeding the whole CRT decode chain. Any
    /// fixture within range of the reader's screen quad would brighten the decoded
    /// channel values, and the failure would present as nonsense DMX rather than as
    /// a rendering bug.
    ///
    /// Everything here is decided package-side from what VRSL already owns. No host
    /// cooperation, no assumptions about how the surrounding application sets its
    /// cameras up.
    /// </summary>
    public static class VRSLCameraFilter
    {
        // Cameras that drive VRSL's own DMX screen readers. Registered by
        // VRSL_CameraConfigurator so the per-frame check is a set lookup rather
        // than a GetComponent, and so the decision never depends on naming or on
        // guessing from camera settings.
        static readonly HashSet<Camera> s_DataReaderCameras = new();

        /// <summary>Mark a camera as feeding VRSL's own data path, so VRSL never
        /// renders lights into it.</summary>
        public static void RegisterDataReader(Camera cam)
        {
            if (cam != null) s_DataReaderCameras.Add(cam);
        }

        public static void UnregisterDataReader(Camera cam)
        {
            if (cam != null) s_DataReaderCameras.Remove(cam);
        }

        /// <param name="ownedSources">
        /// Render textures this manager consumes. A camera rendering into one of
        /// them is rejected outright — a second line of defence for reader rigs
        /// assembled without <c>VRSL_CameraConfigurator</c>.
        /// </param>
        public static VRSLCameraDecision Evaluate(Camera cam, SecondaryCameraMode mode,
                                                  IReadOnlyList<Texture> ownedSources)
        {
            if (cam == null) return VRSLCameraDecision.Skip;

            // Reflection probes and editor previews render through the same pipeline
            // event but don't want stage lights — cost, and polluted captures.
            if (cam.cameraType == CameraType.Reflection
             || cam.cameraType == CameraType.Preview) return VRSLCameraDecision.Skip;

            if (s_DataReaderCameras.Contains(cam)) return VRSLCameraDecision.Skip;

            var target = cam.targetTexture;

            // No target texture means the player's view, including XR where the
            // swapchain is handled outside the camera. Always the full treatment.
            if (target == null) return VRSLCameraDecision.Full;

            if (ownedSources != null)
            {
                for (int i = 0; i < ownedSources.Count; i++)
                    if (ownedSources[i] != null && ReferenceEquals(ownedSources[i], target))
                        return VRSLCameraDecision.Skip;
            }

            return mode switch
            {
                SecondaryCameraMode.Skip        => VRSLCameraDecision.Skip,
                SecondaryCameraMode.SurfaceOnly => VRSLCameraDecision.SurfaceOnly,
                _                               => VRSLCameraDecision.Full,
            };
        }
    }
}
