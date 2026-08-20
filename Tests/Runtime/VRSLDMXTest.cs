using NUnit.Framework;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Shared setup for the DMX rows.
    /// </summary>
    abstract class VRSLDMXTest
    {
        // The host's own logging is off for the fixture's life, so the only
        // errors a row can see are the package's, and those fail it the ordinary
        // way. Once per fixture rather than per row: the host logs between rows
        // too, and a message outside any row's scope is nobody's failure.
        [OneTimeSetUp]
        public void QuietTheHost() => VRSLHostQuiet.Silence();

        [OneTimeTearDown]
        public void LetTheHostSpeak() => VRSLHostQuiet.Restore();

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
