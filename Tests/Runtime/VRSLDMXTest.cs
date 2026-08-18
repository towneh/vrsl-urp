using System.Collections;
using System.Linq;
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
        /// Suppression has to start before the test body, not just inside it. The rig
        /// sets the same flag, but only once <c>Build</c> runs, which leaves the frames
        /// between one row ending and the next one building unguarded — and that is
        /// where the host project's error kept landing.
        ///
        /// <c>[UnitySetUp]</c> rather than <c>[SetUp]</c>: a plain setup runs before
        /// the runner opens the log scope for a <c>[UnityTest]</c>, so the flag it sets
        /// is discarded. No frame is advanced here, because a frame yielded from setup
        /// is itself outside the scope.
        /// </summary>
        [UnitySetUp]
        public IEnumerator IgnoreHostProjectLogs()
        {
            LogAssert.ignoreFailingMessages = true;
            yield break;
        }

        [TearDown]
        public void FailOnVRSLErrors()
        {
            var errors = VRSLDMXRig.CollectedErrors;
            string joined = string.Join(" | ", errors);
            VRSLDMXRig.ClearCollectedErrors();
            // The blanket suppression above is what lets the host project's logs
            // through; this puts the package's own back. Without it a row could go
            // green while VRSL was reporting a shader that failed to compile.
            Assert.IsEmpty(joined, $"VRSL logged errors during this row: {joined}");
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
