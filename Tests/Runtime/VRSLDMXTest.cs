using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Shared setup for the DMX rows.
    /// </summary>
    abstract class VRSLDMXTest
    {
        /// <summary>
        /// The project these run in is a Basis client, and its own systems log errors
        /// about a missing scene once they have been ticking for a while. The test
        /// framework fails any test that sees an unhandled error log, which failed
        /// the rows that step thousands of frames and left the short ones passing —
        /// flakiness, to look at, and nothing to do with what they measure.
        ///
        /// <c>[UnitySetUp]</c> rather than <c>[SetUp]</c>: a plain setup runs before
        /// the runner opens the log scope for a <c>[UnityTest]</c>, so the flag it
        /// sets is discarded.
        /// </summary>
        [UnitySetUp]
        public IEnumerator IgnoreHostProjectLogs()
        {
            LogAssert.ignoreFailingMessages = true;
            // No frame is advanced here on purpose. The runner resets the flag
            // around setup, so a frame yielded from inside it is the one window
            // where a host-project error can still land unguarded.
            yield break;
        }

        /// <summary>Half a byte step. Channel values arrive as bytes, so anything
        /// smaller than this is float representation and anything larger is a
        /// different channel.</summary>
        protected const float Half = 0.5f / 255f;

        /// <summary>Compare decoded colour with a byte-step tolerance. The shader
        /// hands back <c>half</c>, so a channel of 7 does not come home as exactly
        /// <c>7f / 255f</c> and an equality check fails on values that print the
        /// same to three decimals.</summary>
        protected static void AssertNear(Vector3 expected, Vector3 actual, string because)
        {
            Assert.AreEqual(expected.x, actual.x, Half, because + " (red)");
            Assert.AreEqual(expected.y, actual.y, Half, because + " (green)");
            Assert.AreEqual(expected.z, actual.z, Half, because + " (blue)");
        }

        protected static bool Near(Vector3 a, Vector3 b)
            => Mathf.Abs(a.x - b.x) < Half
            && Mathf.Abs(a.y - b.y) < Half
            && Mathf.Abs(a.z - b.z) < Half;
    }
}
