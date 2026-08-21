using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Compiles every shader and compute shader in the package and reports the
    /// errors, without needing a scene set up to request them.
    ///
    /// A broken fullscreen pass draws nothing rather than drawing wrong, so a
    /// shader that fails to compile presents as "VRSL lighting stopped working"
    /// with no obvious cause — the Console entry is easy to miss among a project's
    /// normal noise. This turns that into one command.
    ///
    /// <b>What it does not cover.</b> Editor shader compilation is lazy: Unity
    /// compiles the variants the importer asks for, not the full keyword matrix.
    /// Errors confined to a variant only a particular scene requests — a specific
    /// stereo mode, a keyword combination — can still get through. This catches
    /// base-variant failures, which is the common case and includes anything
    /// structural like an unrollable loop or an undefined identifier.
    /// </summary>
    public static class VRSL_ShaderValidation
    {
        const string Menu        = "VRSL/URP/Validate Shaders";
        const string PackageRoot = "Packages/town.mr.vrsl-urp";

        [MenuItem(Menu, false, 101)]
        public static void ValidateFromMenu()
        {
            var report = new StringBuilder();
            int errors = Validate(report, out int shaderCount);

            string summary = errors == 0
                ? $"No errors across {shaderCount} shader(s)."
                : $"{errors} error(s) across {shaderCount} shader(s). Full detail in the Console.";

            if (errors > 0) Debug.LogError("[VRSL] Shader validation\n" + report);
            else            Debug.Log("[VRSL] Shader validation\n" + report);

            EditorUtility.DisplayDialog("VRSL Shader Validation", summary, "OK");
        }

        /// <summary>
        /// Entry point for <c>-batchmode -executeMethod</c>. Exits 1 when any
        /// shader failed, so it can gate a commit or a build.
        /// </summary>
        public static void ValidateFromCommandLine()
        {
            var report = new StringBuilder();
            int errors = Validate(report, out int shaderCount);

            Debug.Log("[VRSL] Shader validation\n" + report);
            Debug.Log(errors == 0
                ? $"[VRSL] PASS — {shaderCount} shader(s), no errors."
                : $"[VRSL] FAIL — {errors} error(s) across {shaderCount} shader(s).");

            EditorApplication.Exit(errors == 0 ? 0 : 1);
        }

        /// <summary>Compiles everything and appends findings. Returns the error count.</summary>
        public static int Validate(StringBuilder report, out int shaderCount)
        {
            var shaders  = LoadAll<Shader>("t:Shader");
            var computes = LoadAll<ComputeShader>("t:ComputeShader");

            shaderCount = shaders.Count + computes.Count;
            int errors = 0;

            foreach (var shader in shaders)
            {
                string path = AssetDatabase.GetAssetPath(shader);
                // Reimport to force a compile — GetShaderMessages only reports
                // what the last compilation produced, which may be nothing at all
                // if the shader hasn't been asked for since the editor opened.
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                errors += Append(report, path, ShaderUtil.GetShaderMessages(shader));
            }

            foreach (var compute in computes)
            {
                string path = AssetDatabase.GetAssetPath(compute);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                errors += Append(report, path, ShaderUtil.GetComputeShaderMessages(compute));
            }

            if (errors == 0) report.AppendLine($"No errors across {shaderCount} shader(s).");
            return errors;
        }

        static int Append(StringBuilder report, string path, ShaderMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return 0;

            int errors = 0;
            foreach (var m in messages)
            {
                bool isError = m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error;
                if (isError) errors++;

                report.AppendLine($"{(isError ? "ERROR" : "warn ")} {path}({m.line}) [{m.platform}]");
                report.AppendLine($"        {m.message}");
                if (!string.IsNullOrEmpty(m.messageDetails))
                    report.AppendLine($"        {m.messageDetails.Trim()}");
            }
            return errors;
        }

        static List<T> LoadAll<T>(string filter) where T : Object
        {
            return AssetDatabase.FindAssets(filter, new[] { PackageRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .Select(AssetDatabase.LoadAssetAtPath<T>)
                .Where(a => a != null)
                .ToList();
        }
    }
}
