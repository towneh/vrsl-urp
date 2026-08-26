using System;
using System.IO;
using NUnit.Framework;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows for how the committed reference run is located.
    ///
    /// The variable is process-wide and <c>VRSLImageCompare.GoldenFolder</c> reads it
    /// too, so every row here restores it in teardown rather than at the end of the
    /// row — an assertion that fails part way through would otherwise leave the image
    /// rows pointed at a temporary folder for the rest of the session.
    ///
    /// Row H7 of TESTING.md.
    /// </summary>
    class VRSLBaselineReferenceTests
    {
        const string Variable = "VRSL_PERF_HOME";

        string _restore;
        string _temp;

        [SetUp]
        public void SetUp()
        {
            _restore = Environment.GetEnvironmentVariable(Variable);
            _temp    = Path.Combine(Path.GetTempPath(), "vrsl-baseline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(Variable, _restore);
            if (_temp != null && Directory.Exists(_temp)) Directory.Delete(_temp, true);
        }

        [Test]
        public void NoHomeMeansNoReference()
        {
            Environment.SetEnvironmentVariable(Variable, null);
            Assert.IsNull(VRSLBaseline.ReferencePath,
                "With the variable unset there is no reference, and a caller has to say so "
              + "rather than compare against nothing.");
        }

        [Test]
        public void HomeWithoutABaselineMeansNoReference()
        {
            // The case a consuming project lands in: the variable is set for the golden
            // frames and no run has been committed beside them. Returning the path
            // regardless would turn that into "no such run" further down, which sends the
            // reader looking for a file rather than for a baseline they never had.
            Environment.SetEnvironmentVariable(Variable, _temp);
            Assert.IsNull(VRSLBaseline.ReferencePath);
        }

        [Test]
        public void HomeWithABaselineResolvesToIt()
        {
            string expected = Path.Combine(_temp, "baseline.json");
            File.WriteAllText(expected, "{}");
            Environment.SetEnvironmentVariable(Variable, _temp);

            Assert.AreEqual(Path.GetFullPath(expected), Path.GetFullPath(VRSLBaseline.ReferencePath));
        }

        [Test]
        public void HomeIsReportedEvenWhenTheBaselineIsAbsent()
        {
            // ReferenceHome exists so a failure message can name the folder that was
            // looked in. It has to answer while ReferencePath does not, or the message
            // degrades to "not found" with no path in it.
            Environment.SetEnvironmentVariable(Variable, _temp);
            Assert.IsNull(VRSLBaseline.ReferencePath);
            Assert.AreEqual(_temp, VRSLBaseline.ReferenceHome);
        }
    }
}
