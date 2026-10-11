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
    /// The fixture shaders compiled for a platform the editor is not running on. Row N34.
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
    }
}
#endif
