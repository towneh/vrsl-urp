using System.Collections.Generic;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>How VRSL treats cameras that render into a texture rather than to
    /// the player's view — mirrors, portals, and camera props. Declared in cost
    /// order; the values are fixed so a scene keeps the choice it was saved with.</summary>
    public enum SecondaryCameraMode
    {
        /// <summary>Light them exactly like the main view, at the scene's own
        /// quality level.</summary>
        Match = 0,

        /// <summary>Light them one quality level below the scene's, so beams stay
        /// in a mirror at a lower price. The default. See
        /// <see cref="VRSLQualityLevel.Below"/> for what each level steps down to.</summary>
        Reduced = 3,

        /// <summary>Run surface lighting but skip the volumetric raymarch, which is
        /// by far the more expensive of the two.</summary>
        SurfaceOnly = 1,

        /// <summary>No VRSL passes at all. Cheapest, and visibly wrong in a mirror
        /// pointed at the rig.</summary>
        Skip = 2,
    }

    /// <summary>What VRSL should do for a given camera, and at what level.</summary>
    public readonly struct VRSLCameraDecision
    {
        /// <summary>Whether any VRSL pass runs on the camera.</summary>
        public readonly bool        Render;
        /// <summary>Whether the volumetric pass runs, where the level draws beams.</summary>
        public readonly bool        Volumetrics;
        /// <summary>The level the camera's passes take their costs from.</summary>
        public readonly VRSLQuality Quality;

        public VRSLCameraDecision(bool render, bool volumetrics, VRSLQuality quality)
        {
            Render      = render;
            Volumetrics = volumetrics;
            Quality     = quality;
        }

        /// <summary>No passes. The level is meaningless and left at Off.</summary>
        public static VRSLCameraDecision Skip => default;

        public override string ToString() =>
            !Render ? "Skip" : Volumetrics ? $"Full at {Quality}" : $"SurfaceOnly at {Quality}";
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
    /// cameras up. Stateless per camera, so mirrors that come and go at runtime need
    /// nothing reset.
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

        // Cameras that render into a texture and are nonetheless the player's view:
        // a stream or spectator camera whose texture goes to a screen, or a harness
        // measuring the main view off-screen. The policy for secondary cameras does
        // not apply to them.
        static readonly HashSet<Camera> s_MainViews = new();

        /// <summary>Treat <paramref name="cam"/> as the player's view even though it
        /// renders into a texture: lit in full at the scene's level, whatever the
        /// secondary-camera policy says. A camera rendering into a texture the manager
        /// consumes is still skipped.</summary>
        public static void RegisterMainView(Camera cam)
        {
            if (cam != null) s_MainViews.Add(cam);
        }

        public static void UnregisterMainView(Camera cam)
        {
            if (cam != null) s_MainViews.Remove(cam);
        }

        /// <param name="sceneQuality">The manager's own level, which the player's
        /// view always renders at and a secondary camera renders at or below.</param>
        /// <param name="ownedSources">
        /// Render textures this manager consumes. A camera rendering into one of
        /// them is rejected outright — a second line of defence for reader rigs
        /// assembled without <c>VRSL_CameraConfigurator</c>.
        /// </param>
        public static VRSLCameraDecision Evaluate(Camera cam, SecondaryCameraMode mode,
                                                  VRSLQuality sceneQuality,
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
            if (target == null) return new VRSLCameraDecision(true, true, sceneQuality);

            if (ownedSources != null)
            {
                for (int i = 0; i < ownedSources.Count; i++)
                    if (ownedSources[i] != null && ReferenceEquals(ownedSources[i], target))
                        return VRSLCameraDecision.Skip;
            }

            if (s_MainViews.Contains(cam)) return new VRSLCameraDecision(true, true, sceneQuality);

            return mode switch
            {
                SecondaryCameraMode.Skip        => VRSLCameraDecision.Skip,
                SecondaryCameraMode.SurfaceOnly => new VRSLCameraDecision(true, false, sceneQuality),
                SecondaryCameraMode.Reduced     => new VRSLCameraDecision(true, true, VRSLQualityLevel.Below(sceneQuality)),
                _                               => new VRSLCameraDecision(true, true, sceneQuality),
            };
        }

        /// <summary>What a policy does to a mirror in this scene, in an author's
        /// words. For diagnostics and validation, so the reader is not left to work
        /// out what <c>Reduced</c> steps down to.</summary>
        public static string Describe(SecondaryCameraMode mode, VRSLQuality sceneQuality)
        {
            switch (mode)
            {
                case SecondaryCameraMode.Skip:
                    return "Skip: mirrors and camera props get no VRSL lighting";
                case SecondaryCameraMode.SurfaceOnly:
                    return "SurfaceOnly: mirrors and camera props get surface lighting and no beams";
                case SecondaryCameraMode.Reduced:
                {
                    var below = VRSLQualityLevel.Below(sceneQuality);
                    return below == sceneQuality
                        ? $"Reduced: mirrors and camera props render at {sceneQuality}, the same as "
                        + "the scene, because there is no level below it"
                        : $"Reduced: mirrors and camera props render at {below}, one level below "
                        + $"the scene's {sceneQuality}";
                }
                default:
                    return $"Match: mirrors and camera props render at {sceneQuality}, the same as the scene";
            }
        }
    }
}
