using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

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
        /// The tile list as numbers. Shared by the console diagnostic and the
        /// benchmark harness so both read one routine — a second copy of this loop
        /// is a second place for the stride to go stale.
        /// </summary>
        public readonly struct TileSummary
        {
            /// <summary>False when the cull did not run: no pass, no buffer, or no
            /// active tiles. Every other field is meaningless then.</summary>
            public readonly bool  Engaged;
            public readonly int   Tiles;
            public readonly float Average;
            public readonly int   Max;
            public readonly int   Empty;
            /// <summary>Tiles at the per-tile cap, where fixtures past it are
            /// silently dropped.</summary>
            public readonly int   Capped;
            public readonly long  Total;

            public TileSummary(bool engaged, int tiles, float average, int max, int empty, int capped, long total)
            {
                Engaged = engaged; Tiles = tiles; Average = average;
                Max = max; Empty = empty; Capped = capped; Total = total;
            }

            public float EmptyPercent => Tiles > 0 ? 100f * Empty / Tiles : 0f;
        }

        /// <summary>
        /// Reads the tile list back. One-shot: this stalls on the GPU, so it belongs
        /// in a diagnostic or once per benchmark configuration, never on the frame
        /// path.
        /// </summary>
        public static TileSummary SummariseTiles(VRSLTileCullPass cull)
        {
            if (cull == null || cull.TileBuffer == null
             || cull.TileParams.x < 1f || cull.ActiveTileCount <= 0)
                return new TileSummary(false, 0, 0f, 0, 0, 0, 0);

            int stride = VRSLTileCullPass.Stride;
            int tiles  = cull.ActiveTileCount;

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

            return new TileSummary(true, tiles, (float)total / tiles, max, empty, capped, total);
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

            var summary = SummariseTiles(cull);
            if (!summary.Engaged)
                return "Tile culling: INACTIVE — every pixel iterates all "
                     + $"{fixtureCount} fixture(s). Is lightCullShader assigned?";

            int   tiles   = summary.Tiles;
            long  total   = summary.Total;
            int   max     = summary.Max;
            int   capped  = summary.Capped;
            int   empty   = summary.Empty;
            float average = summary.Average;

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

        /// <summary>How many fixtures are actually emitting, and how brightly. Shared
        /// by the console diagnostic and the benchmark harness.</summary>
        public readonly struct EmissionSummary
        {
            public readonly int   Emitting;
            public readonly int   Total;
            public readonly float Peak;

            public EmissionSummary(int emitting, int total, float peak)
            {
                Emitting = emitting; Total = total; Peak = peak;
            }
        }

        /// <summary>One-shot readback of the decoded light data. Stalls the GPU, so
        /// diagnostics and once-per-configuration only.</summary>
        public static EmissionSummary SummariseEmission(GraphicsBuffer lightData, int fixtureCount)
        {
            if (lightData == null || fixtureCount == 0) return new EmissionSummary(0, 0, 0f);

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
            return new EmissionSummary(emitting, fixtureCount, peak);
        }

        /// <summary>
        /// Summarises decoded light data. Splits "no data reached the lights"
        /// from "data is fine, something downstream is eating it" — the two have
        /// completely different causes and the distinction is not visible on screen.
        /// </summary>
        public static string LightDataStatus(GraphicsBuffer lightData, int fixtureCount)
        {
            if (lightData == null)
                return "Light data: NO BUFFER — the decode never allocated one, so this is "
                     + "upstream of anything to do with fixtures";
            if (fixtureCount == 0)
                return "Light data: NO FIXTURES — the manager collected none, so there is "
                     + "nothing to decode into. Check the fixtures are active and that "
                     + "RefreshFixtures has run since they were added";

            var summary = SummariseEmission(lightData, fixtureCount);
            int emitting = summary.Emitting;
            float peak = summary.Peak;

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
