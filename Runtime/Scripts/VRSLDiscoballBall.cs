using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// Turns a discoball's ball with its dots. Both light managers call it once a frame
    /// for each discoball they hold, with the fixture's <c>lensTransform</c> as the ball.
    /// </summary>
    static class VRSLDiscoballBall
    {
        /// <summary>
        /// Turn the ball by this frame's share of the spin, about the fixture's up axis:
        /// the axis and the sense the dots turn with. By the frame's delta rather than to
        /// an angle, so a change of speed turns it at the new rate instead of jumping it;
        /// the facets have nothing to line up with in the cubemap, so only the rate has
        /// to match. A ball that is the fixture itself is left alone, since turning it
        /// would turn whatever the author hung from the fixture.
        /// </summary>
        internal static void Turn(Transform fixture, Transform ball, float degreesPerSecond, float deltaTime)
        {
            if (ball == null || ball == fixture || degreesPerSecond == 0f) return;
            ball.Rotate(fixture.up, degreesPerSecond * deltaTime, Space.World);
        }
    }
}
