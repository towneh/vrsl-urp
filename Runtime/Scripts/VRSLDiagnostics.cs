using System.Text;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// One-shot health report for a VRSL light manager, reachable from the
    /// component's context menu in play mode.
    ///
    /// The failure modes in this pipeline mostly look identical from the outside:
    /// nothing is lit. That can mean the decode produced no data, or the tile cull
    /// rejected everything, or the surface prepass never ran, or a fullscreen
    /// shader failed to compile and is silently drawing nothing. This separates
    /// those, so the first question after "it's dark" has an answer that doesn't
    /// require a bisect.
    /// </summary>
    public static class VRSLDiagnostics
    {
        /// <summary>Whether a shader will actually render anything.</summary>
        public static bool ShaderUsable(Shader shader)
        {
            if (shader == null) return false;
#if UNITY_EDITOR
            if (UnityEditor.ShaderUtil.ShaderHasError(shader)) return false;
#endif
            return shader.isSupported;
        }

        /// <summary>
        /// A shader that won't run draws nothing rather than drawing wrong, so
        /// this is usually the first line worth reading.
        ///
        /// <c>Shader.isSupported</c> answers "can this run on the end user's
        /// graphics card", which is false for a compile error <i>and</i> for an
        /// unsupported GPU, API or target level. Those need completely different
        /// fixes, so in the editor the two are separated with
        /// <c>ShaderUtil.ShaderHasError</c> rather than guessing at one of them.
        /// </summary>
        public static string ShaderStatus(string label, Shader shader)
        {
            if (shader == null) return $"{label}: NOT ASSIGNED";

#if UNITY_EDITOR
            if (UnityEditor.ShaderUtil.ShaderHasError(shader))
                return $"{label}: '{shader.name}' HAS COMPILE ERRORS — it will draw nothing. "
                     + "Run VRSL → URP → Validate Shaders for the messages.";

            if (!shader.isSupported)
                return $"{label}: '{shader.name}' compiles but is UNSUPPORTED on this GPU/API — "
                     + "it will draw nothing. Check the shader's target level and requirements.";
#else
            if (!shader.isSupported)
                return $"{label}: '{shader.name}' WILL NOT RUN — it will draw nothing. Either a "
                     + "compile error or an unsupported GPU/API; the Console distinguishes them.";
#endif

            return $"{label}: '{shader.name}' ok";
        }

        /// <summary>
        /// <c>HasKernel</c> only reports whether the name is present in the
        /// compiled shader, so its absence could be a wrong name, a compile
        /// failure, or a stripped kernel. The message says so rather than
        /// asserting one of them.
        ///
        /// Takes every kernel the caller depends on, not just one: reporting a
        /// compute as ok while a pass rejects it for a missing second kernel is
        /// the kind of half-truth this tool exists to remove.
        /// </summary>
        public static string ComputeStatus(string label, ComputeShader compute, params string[] kernels)
        {
            if (compute == null) return $"{label}: NOT ASSIGNED";

#if UNITY_EDITOR
            foreach (var m in UnityEditor.ShaderUtil.GetComputeShaderMessages(compute))
            {
                if (m.severity != UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error) continue;
                return $"{label}: '{compute.name}' HAS COMPILE ERRORS — {m.message.Trim()}";
            }
#endif

            foreach (string kernel in kernels)
            {
                if (compute.HasKernel(kernel)) continue;
                return $"{label}: '{compute.name}' has no kernel '{kernel}' — check the kernel "
                     + "name and that the right compute is assigned; the Console will show any "
                     + "compile errors.";
            }

            return $"{label}: '{compute.name}' ok";
        }

        /// <summary>
        /// Reads the tile list back and summarises it. Tells you whether culling
        /// is running at all, how much it's actually saving, and whether the
        /// per-tile cap is being hit — which silently drops fixtures.
        /// </summary>
        public static string TileStatus(VRSLTileCullPass cull, int fixtureCount)
        {
            if (cull == null) return "Tile culling: pass not allocated";
            if (cull.TileBuffer == null) return "Tile culling: no buffer";
            if (cull.TileParams.x < 1f || cull.ActiveTileCount <= 0)
                return "Tile culling: INACTIVE — every pixel iterates all "
                     + $"{fixtureCount} fixture(s). Is lightCullShader assigned?";

            int stride = VRSLTileCullPass.Stride;
            int tiles  = cull.ActiveTileCount;

            // One-shot readback: fine for a diagnostic, never on the frame path.
            var raw = new uint[tiles * stride];
            cull.TileBuffer.GetData(raw, 0, 0, raw.Length);

            long total = 0;
            int max = 0, capped = 0, empty = 0;
            for (int t = 0; t < tiles; t++)
            {
                int count = (int)raw[t * stride];
                total += count;
                if (count > max) max = count;
                if (count == 0) empty++;
                if (count >= VRSLTileCullPass.MaxLightsPerTile) capped++;
            }

            float average = tiles > 0 ? (float)total / tiles : 0f;
            var sb = new StringBuilder();
            if (total == 0)
                return $"Tile culling: active — {tiles} tiles, but no fixture reached any of "
                     + "them. Expected when nothing is emitting; a tiling fault only if "
                     + "fixtures are lit and on screen";
            sb.Append($"Tile culling: active — {tiles} tiles ({cull.TileParams.x}x{cull.TileParams.y} "
                    + $"@ {cull.TileParams.z}px), avg {average:F1} lights/tile, max {max}, "
                    + $"{empty} empty of {fixtureCount} fixture(s)");
            if (capped > 0)
                sb.Append($"\n  WARNING: {capped} tile(s) hit the {VRSLTileCullPass.MaxLightsPerTile}-light "
                        + "cap — fixtures past it are dropped for those tiles");
            return sb.ToString();
        }

        /// <summary>
        /// Summarises decoded light data. Splits "no data reached the lights"
        /// from "data is fine, something downstream is eating it" — the two have
        /// completely different causes and the distinction is not visible on screen.
        /// </summary>
        public static string LightDataStatus(GraphicsBuffer lightData, int fixtureCount)
        {
            if (lightData == null || fixtureCount == 0) return "Light data: no buffer";

            // Mirrors VRSLLightData: 4 x float4, intensity in colorAndIntensity.w.
            var raw = new Vector4[fixtureCount * 4];
            lightData.GetData(raw);

            int emitting = 0;
            float peak = 0f;
            for (int i = 0; i < fixtureCount; i++)
            {
                float intensity = raw[i * 4 + 2].w;
                if (intensity > 0f) emitting++;
                if (intensity > peak) peak = intensity;
            }

            if (emitting == 0)
                return $"Light data: 0/{fixtureCount} emitting — ALL DARK: the decode produced "
                     + "nothing, so this is a data problem, not a rendering one";

            // Enough precision to distinguish "barely lit" from "not lit": at F1 a
            // fixture at 0.04 printed as 0.0 and contradicted the count beside it.
            string faint = peak < 0.05f
                ? " — barely above zero, so the source is probably near-silent rather than off"
                : "";
            return $"Light data: {emitting}/{fixtureCount} emitting, peak intensity {peak:G4}{faint}";
        }

        public static string SurfacePrepassStatus(Shader surfacePropertiesShader)
        {
            if (surfacePropertiesShader == null)
                return "Surface prepass: normals only — surfacePropertiesShader unassigned, "
                     + "every surface lights as neutral mid-grey";

            string status = ShaderStatus("Surface prepass", surfacePropertiesShader);

            // Only claim the capture happened when the shader can actually run —
            // otherwise the line contradicts itself, reporting a failure and a
            // success in the same breath.
            return ShaderUsable(surfacePropertiesShader)
                ? status + " (albedo + smoothness + metallic captured)"
                : status + " — surfaces fall back to neutral mid-grey";
        }
    }
}
