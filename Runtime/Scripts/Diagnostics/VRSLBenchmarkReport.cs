using System;
using System.IO;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// Where a run's files go.
    ///
    /// In the runtime assembly because the standard sweep runs in a built player as
    /// well as in the editor, and a player that measures a matrix and cannot write it
    /// down has measured nothing.
    /// </summary>
    static class VRSLBenchmarkReport
    {
        /// <summary>
        /// Where the timestamped folders are created, or null for beside whatever is
        /// running: the project folder in the editor, the build folder in a player.
        ///
        /// A player run is driven by a script that wants the files somewhere it chose,
        /// and a build folder is a temporary thing it may well delete afterwards.
        /// </summary>
        public static string OutputRoot { get; set; }

        /// <summary>Timestamped, and beside the project rather than inside the
        /// package — the package ships to users and has no business carrying one
        /// machine's timings.</summary>
        public static string Folder(string label)
        {
            string root  = string.IsNullOrEmpty(OutputRoot)
                         ? Directory.GetParent(Application.dataPath)!.FullName
                         : OutputRoot;
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string folder = Path.Combine(root, "VRSL-Benchmarks", $"{stamp}_{label}");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Writes the JSON and the markdown, and returns the folder.</summary>
        public static string Write(VRSLBenchmarkRun run)
        {
            string folder = Folder(run.label);
            File.WriteAllText(Path.Combine(folder, "run.json"), run.ToJson());
            File.WriteAllText(Path.Combine(folder, "report.md"), VRSLBaseline.ToMarkdown(run));
            return folder;
        }
    }
}
