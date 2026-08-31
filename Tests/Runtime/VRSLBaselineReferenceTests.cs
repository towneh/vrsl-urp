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

        [Test]
        public void AMachineIsResolvedByItsOwnFileAndFallsBackWhenItHasNone()
        {
            Environment.SetEnvironmentVariable(Variable, _temp);
            File.WriteAllText(Path.Combine(_temp, "baseline.json"), "{}");

            string mine = Path.Combine(_temp, "baselines",
                                       VRSLBaseline.ReferenceFileName("Test GPU 9000", "Player"));
            Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
            File.WriteAllText(mine, "{}");

            Assert.AreEqual(mine, VRSLBaseline.ReferenceFor("Test GPU 9000", "Player"),
                "a machine with a file of its own did not get it");

            // The same GPU in the other lineage has no file, so it falls back rather than
            // reading the player's numbers as though they were the editor's.
            Assert.AreEqual(Path.Combine(_temp, "baseline.json"),
                            VRSLBaseline.ReferenceFor("Test GPU 9000", "Editor"),
                "a lineage with no file of its own did not fall back");
        }

        /// <summary>
        /// The context is read out of a candidate run.json rather than being a constant,
        /// so it is input. Without reducing it, a context naming a traversal walks out of
        /// the baselines folder and picks whatever JSON it lands on — and the answer is a
        /// comparison against a file nobody chose, not an error.
        /// </summary>
        [Test]
        public void AHostileContextCannotReachOutsideTheBaselinesFolder()
        {
            Environment.SetEnvironmentVariable(Variable, _temp);

            // Somewhere a traversal would land, holding something that would load.
            string outside = Path.Combine(_temp, "outside.json");
            File.WriteAllText(outside, "{}");

            foreach (string hostile in new[]
                     { "../outside", @"..\outside", "/etc/passwd", "..", "a/../../outside" })
            {
                string name = VRSLBaseline.ReferenceFileName("gpu", hostile);
                Assert.That(name, Does.Not.Contain("/").And.Not.Contain("\\"),
                    $"'{hostile}' left a path separator in the file name");
                Assert.That(name, Does.Not.Contain(".."),
                    $"'{hostile}' left a traversal in the file name");
                Assert.AreEqual(name, Path.GetFileName(name),
                    $"'{hostile}' produced more than one path segment");

                // And end to end: it must never resolve to the file outside the folder.
                Assert.AreNotEqual(outside, VRSLBaseline.ReferenceFor("gpu", hostile),
                    $"'{hostile}' reached a file outside the baselines folder");
            }
        }

        [Test]
        public void AnEmptyGpuOrContextStillNamesAFile()
        {
            // Both halves come from a stored run, and a run written before a field
            // existed carries it empty. That must name a file rather than throw or
            // produce something a filesystem refuses.
            Assert.AreEqual("unknown-Editor.json", VRSLBaseline.ReferenceFileName(null, null));
            Assert.AreEqual("unknown-Player.json", VRSLBaseline.ReferenceFileName("", "Player"));
        }
    }
}
