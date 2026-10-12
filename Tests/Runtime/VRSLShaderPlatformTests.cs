#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The package's shaders compiled for a platform the editor is not running on. Rows N34,
    /// T1 and T2.
    /// </summary>
    class VRSLShaderPlatformTests
    {
        /// <summary>
        /// The shaders that read the DMX channel buffers, and the passes that read them: a
        /// mover head reads them in every pass, since its depth and shadow follow its pose.
        /// The Opaque surface and the lens flare read DMX only under a channel-mode keyword
        /// (the flare's list has no empty entry, so one is always on), so they compile with
        /// the 13-channel one.
        /// </summary>
        static readonly (string shader, bool everyPass, string mode)[] BufferReaders =
        {
            ("VRSL-URP/Standard Static/Surface Shaders/Opaque",                  false, "_13CH_MODE"),
            ("VRSL-URP/Standard Static/Surface Shaders/Opaque Multi-Channel Bar", false, null),
            ("VRSL-URP/Basic Laser/DMX",                                          false, null),
            ("VRSL-URP/Standard Static/Lens Flare",                               false, "_13CH_MODE"),
            ("VRSL-URP/Standard Mover/Fixture",                                   true,  null),
            ("VRSL-URP/Wash Mover/Fixture",                                       true,  null),
        };

        /// <summary>What a pass binds when it reads a channel source.</summary>
        static readonly string[] Buffers =
            { "_VRSLU_DMXChannels", "_VRSLU_DMXMovement", "_VRSLU_DMXSpinPhase", "_VRSLU_DMXStrobePhase" };

        /// <summary>Instancing, and multiview for a headset rendering both eyes in one pass.</summary>
        static readonly string[][] KeywordSets =
        {
            new string[0],
            new[] { "INSTANCING_ON" },
            new[] { "INSTANCING_ON", "STEREO_MULTIVIEW_ON" },
        };

        /// <summary>
        /// N34. Every pass of every shader that reads the channel
        /// buffer compiles for Android on Vulkan, plain, instanced and instanced with
        /// multiview, and the passes that read the buffers still bind them there: the shader
        /// model the buffers need isn't lowered without a warning, which would leave the fixture on
        /// the grid on Quest. Only passes that include the DMX read functions are compiled;
        /// the others, among them those borrowed from URP's Lit shader, read no DMX.
        /// </summary>
        [Test]
        public void N34_the_buffer_reading_shaders_compile_for_android_vulkan_and_keep_the_buffer()
        {
            var failures = new List<string>();
            int compiled = 0;
            foreach (var (name, everyPass, mode) in BufferReaders)
            {
                var shader = Shader.Find(name);
                Assert.IsNotNull(shader, $"{name} is not in this project");
                var sub = ShaderUtil.GetShaderData(shader).GetSubshader(0);
                bool anyBinds = false;
                for (int p = 0; p < sub.PassCount; p++)
                {
                    var pass = sub.GetPass(p);
                    if (pass.SourceCode == null || !pass.SourceCode.Contains("VRSL-DMXFunctions")) continue;
                    foreach (var set in KeywordSets)
                    {
                        var keywords = mode == null ? set : set.Append(mode).ToArray();
                        var info = pass.CompileVariant(ShaderType.Vertex, keywords, ShaderCompilerPlatform.Vulkan,
                                                       BuildTarget.Android);
                        compiled++;
                        string where = $"{name}, pass {p} '{pass.Name}', [{string.Join(" ", keywords)}]";
                        var errors = info.Messages.Where(m => m.severity == ShaderCompilerMessageSeverity.Error).ToList();
                        if (!info.Success || errors.Count > 0)
                        {
                            failures.Add($"{where} does not compile: "
                                       + string.Join("; ", errors.Select(m => $"{m.message} ({m.file}:{m.line})")));
                            continue;
                        }
                        bool binds = info.ResourceBindings.Any(b => Buffers.Contains(b.Name));
                        anyBinds |= binds;
                        if (everyPass && !binds) failures.Add($"{where} binds none of the channel buffers");
                    }
                }
                if (!anyBinds) failures.Add($"{name}: no pass binds the channel buffers");
            }
            Debug.Log($"[N34] {compiled} variants compiled for Android Vulkan");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        const string PackageRuntime = "Packages/town.mr.vrsl-urp/Runtime";

        static List<Shader> PackageShaders()
            => AssetDatabase.FindAssets("t:Shader", new[] { PackageRuntime })
                            .Select(g => AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(g)))
                            .Where(s => s != null)
                            .ToList();

        /// <summary>
        /// The subshaders URP picks from, with their index: those tagged with the active
        /// pipeline's own RenderPipeline value where any is, otherwise the first. A Built-in
        /// subshader left beside a URP one never runs under URP, so compiling it would only
        /// report noise.
        /// </summary>
        static IEnumerable<(int index, ShaderData.Subshader sub)> UrpSubshaders(ShaderData data)
        {
            var tag = new UnityEngine.Rendering.ShaderTagId("RenderPipeline");
            string value = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.renderPipelineShaderTag
                           ?? "UniversalPipeline";
            var all = Enumerable.Range(0, data.SubshaderCount).Select(i => (i, data.GetSubshader(i))).ToList();
            var urp = all.Where(s => s.Item2.FindTagValue(tag).name == value).ToList();
            return urp.Count > 0 ? urp : all.Take(1);
        }

        static readonly System.Text.RegularExpressions.Regex KeywordPragma = new(
            @"^\s*#pragma\s+(multi_compile|shader_feature)(?:_local)?(?:_vertex|_fragment)?\s+(.+)$",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>
        /// The keywords a pass always has on: the first of each list it declares without an
        /// empty entry, which is the variant Unity builds when nothing else asks. Compiling
        /// with none of them would build a variant that never exists. A shader_feature with a
        /// single keyword is a switch (it implies the empty entry), so it adds nothing.
        /// </summary>
        static IEnumerable<string> AlwaysOnKeywords(string source)
        {
            foreach (System.Text.RegularExpressions.Match m in KeywordPragma.Matches(source))
            {
                var tokens = m.Groups[2].Value.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0 || tokens[0].All(c => c == '_')) continue;
                if (m.Groups[1].Value == "shader_feature" && tokens.Length == 1) continue;
                yield return tokens[0];
            }
        }

        /// <summary>
        /// Every pass of every shader in the package, vertex and fragment, compiled for
        /// <paramref name="target"/> on Vulkan under each keyword set. A pass borrowed with
        /// <c>UsePass</c> can't be compiled through the shader that borrows it; Unity logs
        /// that it is a UsePass, naming the subshader and pass index. Only a pass Unity names
        /// that way is left to URP and listed in <paramref name="borrowed"/>; every other
        /// error, logged or returned, is a failure. <paramref name="compiled"/> counts the
        /// programs actually compiled. Returns the failures.
        /// </summary>
        static List<string> CompileForVulkan(BuildTarget target, string[][] keywordSets, out int shaders, out int compiled,
                                             out List<string> borrowed)
        {
            var failures = new List<string>();
            var leftToUrp = new List<string>();
            var logged = new List<string>();
            void OnLog(string condition, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception) logged.Add(condition);
            }
            // The UsePass errors are expected and judged below, so the framework mustn't
            // fail the test on them; anything else logged becomes a failure here instead.
            bool ignoreFailingWas = UnityEngine.TestTools.LogAssert.ignoreFailingMessages;
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            Application.logMessageReceived += OnLog;
            var found = PackageShaders();
            shaders = found.Count;
            compiled = 0;
            try
            {
                foreach (var shader in found)
                {
                    var data = ShaderUtil.GetShaderData(shader);
                    foreach (var (s, sub) in UrpSubshaders(data))
                        for (int p = 0; p < sub.PassCount; p++)
                        {
                            logged.Clear();
                            var pass = sub.GetPass(p);
                            string where = $"{shader.name}, subshader {s} pass {p} '{pass.Name}'";
                            string usePass = $"Shader {shader.name}: tried to access a pass with index {p} in subshader {s} "
                                           + "that is either a UsePass or a GrabPass";
                            string source = pass.SourceCode;
                            if (logged.Any(l => l.Contains(usePass)))
                            {
                                leftToUrp.Add($"{shader.name} '{pass.Name}'");
                                continue;
                            }
                            if (string.IsNullOrEmpty(source))
                            {
                                failures.Add($"{where} has no source to compile");
                                continue;
                            }
                            var alwaysOn = AlwaysOnKeywords(source).ToArray();
                            bool borrowedPass = false;
                            foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
                            {
                                if (borrowedPass || !pass.HasShaderStage(stage)) continue;
                                foreach (var set in keywordSets)
                                {
                                    var keywords = alwaysOn.Concat(set).Distinct().ToArray();
                                    logged.Clear();
                                    var info = pass.CompileVariant(stage, keywords, ShaderCompilerPlatform.Vulkan, target);
                                    if (!info.Success && logged.Any(l => l.Contains(usePass)))
                                    {
                                        leftToUrp.Add($"{shader.name} '{pass.Name}'");
                                        borrowedPass = true;
                                        break;
                                    }
                                    compiled++;
                                    var errors = info.Messages.Where(m => m.severity == ShaderCompilerMessageSeverity.Error)
                                                              .Select(m => $"{m.message} ({m.file}:{m.line})")
                                                              .Concat(logged.Select(l => $"logged: {l}"))
                                                              .ToList();
                                    if (info.Success && errors.Count == 0) continue;
                                    failures.Add($"{where}, {stage}, [{string.Join(" ", keywords)}]: "
                                               + (errors.Count > 0 ? string.Join("; ", errors) : "failed with no message"));
                                }
                            }
                        }
                }
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = ignoreFailingWas;
            }
            borrowed = leftToUrp;
            return failures;
        }

        /// <summary>
        /// T1. Every shader in the package compiles for Android on Vulkan, which is what a
        /// Quest runs: each pass's vertex and fragment programs, plain, with multiview (how a
        /// Quest renders both eyes in one pass) and instanced, without switching the editor's
        /// platform. The headless shader validation compiles only the editor's own platform.
        /// </summary>
        [Test]
        public void T1_every_shader_compiles_for_android_vulkan()
        {
            var failures = CompileForVulkan(BuildTarget.Android, new[]
            {
                new string[0],
                new[] { "STEREO_MULTIVIEW_ON" },
                new[] { "INSTANCING_ON" },
            }, out int shaders, out int compiled, out var borrowed);
            Debug.Log($"[T1] {shaders} shaders, {compiled} programs compiled for Android Vulkan, {failures.Count} failed; "
                    + $"{borrowed.Count} borrowed passes left to URP: {string.Join(", ", borrowed)}");
            Assert.Greater(shaders, 0, $"no shaders found under {PackageRuntime}");
            Assert.Greater(compiled, 0, "no program was compiled, so the row checked nothing");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        /// <summary>
        /// T2. Every shader in the package compiles for desktop Linux on Vulkan: each pass's
        /// vertex and fragment programs, plain and instanced, without switching the editor's
        /// platform. Linux runs in desktop mode, so there is no stereo variant.
        /// </summary>
        [Test]
        public void T2_every_shader_compiles_for_linux_vulkan()
        {
            var failures = CompileForVulkan(BuildTarget.StandaloneLinux64, new[]
            {
                new string[0],
                new[] { "INSTANCING_ON" },
            }, out int shaders, out int compiled, out var borrowed);
            Debug.Log($"[T2] {shaders} shaders, {compiled} programs compiled for Linux Vulkan, {failures.Count} failed; "
                    + $"{borrowed.Count} borrowed passes left to URP: {string.Join(", ", borrowed)}");
            Assert.Greater(shaders, 0, $"no shaders found under {PackageRuntime}");
            Assert.Greater(compiled, 0, "no program was compiled, so the row checked nothing");
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }
    }
}
#endif
