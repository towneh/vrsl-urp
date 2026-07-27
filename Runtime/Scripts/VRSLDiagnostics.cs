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
        /// <summary>A shader that failed to compile draws nothing rather than
        /// drawing wrong, so this is usually the first line worth reading.</summary>
        public static string ShaderStatus(string label, Shader shader)
        {
            if (shader == null) return $"{label}: NOT ASSIGNED";
            if (!shader.isSupported) return $"{label}: '{shader.name}' FAILED TO COMPILE — nothing will draw";
            return $"{label}: '{shader.name}' ok";
        }

        public static string ComputeStatus(string label, ComputeShader compute, string kernel)
        {
            if (compute == null) return $"{label}: NOT ASSIGNED";
            if (!compute.HasKernel(kernel))
                return $"{label}: '{compute.name}' MISSING KERNEL '{kernel}' — likely a compile failure";
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

            string verdict = emitting == 0
                ? " — ALL DARK: the decode produced nothing, so this is a data problem, not a rendering one"
                : "";
            return $"Light data: {emitting}/{fixtureCount} emitting, peak intensity {peak:F1}{verdict}";
        }

        public static string SurfacePrepassStatus(Shader surfacePropertiesShader)
        {
            if (surfacePropertiesShader == null)
                return "Surface prepass: normals only — surfacePropertiesShader unassigned, "
                     + "every surface lights as neutral mid-grey";
            return ShaderStatus("Surface prepass", surfacePropertiesShader)
                 + " (albedo + smoothness + metallic captured)";
        }
    }
}
