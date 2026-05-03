using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Menu shortcut for importing the Realtime Light Profiling sample
    /// shipped under <c>Samples~/RealtimeLightProfiling</c>. Wraps the
    /// Package Manager's <see cref="Sample"/> API so authors can pull the
    /// profiling utilities into the project from the VRSL menu without
    /// opening the Package Manager window.
    /// </summary>
    public static class VRSL_URPProfilingSampleMenu
    {
        const string MENU_PATH    = "VRSL/Profiling/Import Profiling Sample";
        const string PACKAGE_NAME = "net.towneh.vrsl-urp";
        const string SAMPLE_NAME  = "Realtime Light Profiling";

        [MenuItem(MENU_PATH)]
        public static void ImportSample()
        {
            // Empty version string → resolve against the currently-installed
            // package version, so this stays correct as the package is bumped.
            var samples = Sample.FindByPackage(PACKAGE_NAME, string.Empty).ToList();
            if (samples.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "VRSL Profiling Sample",
                    $"No samples are declared by package '{PACKAGE_NAME}'. Make sure the " +
                    "package is installed via the Package Manager, not as a loose folder " +
                    "in Assets/.",
                    "OK");
                return;
            }

            var sample = samples.FirstOrDefault(s => s.displayName == SAMPLE_NAME);
            if (sample.displayName != SAMPLE_NAME)
            {
                EditorUtility.DisplayDialog(
                    "VRSL Profiling Sample",
                    $"Could not find the '{SAMPLE_NAME}' sample in package '{PACKAGE_NAME}'. " +
                    "The sample folder may be missing.",
                    "OK");
                return;
            }

            if (sample.isImported)
            {
                bool reimport = EditorUtility.DisplayDialog(
                    "VRSL Profiling Sample",
                    $"'{SAMPLE_NAME}' is already imported at:\n\n{sample.importPath}\n\n" +
                    "Re-import to overwrite the existing copy with the package's current version?",
                    "Re-import", "Cancel");
                if (!reimport) return;
            }

            bool ok = sample.Import(Sample.ImportOptions.OverridePreviousImports);
            if (!ok)
            {
                Debug.LogError(
                    $"[VRSL] Failed to import sample '{SAMPLE_NAME}' from package '{PACKAGE_NAME}'.");
                return;
            }

            AssetDatabase.Refresh();
            Debug.Log(
                $"[VRSL] Imported sample '{SAMPLE_NAME}' to {sample.importPath}. " +
                $"Open VRSL → Profiling → Build Profiling Scene once compilation finishes.");
        }
    }
}
