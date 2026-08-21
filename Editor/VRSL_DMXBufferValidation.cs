using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Checks that a channel published on the CPU is the channel a shader reads.
    ///
    /// The failure this exists to catch is an off-by-one or a packing mistake:
    /// both leave every fixture lit and moving, just reading its neighbour's
    /// values, which looks like a lighting design decision rather than a fault.
    /// Comparing against a ramp that never repeats on a fixture boundary makes a
    /// shift of one channel as visible as a shift of a hundred.
    ///
    /// Menu: VRSL → URP → DMX Config → Validate DMX Channel Buffer. Play mode only, since the
    /// buffer is uploaded per frame by the manager.
    /// </summary>
    public static class VRSL_DMXBufferValidation
    {
        const int MaxReport = 8;

        [MenuItem("VRSL/URP/DMX Config/Validate DMX Channel Buffer", false, 221)]
        public static void Validate()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("VRSL URP",
                    "Enter Play mode first. The channel buffer is uploaded per frame by the manager, "
                    + "so there is nothing to read back while the scene is stopped.", "OK");
                return;
            }

            var mgr = VRSL_URPLightManager.Instance;
            if (mgr == null || mgr.ChannelBuffer == null)
            {
                Debug.LogError("[VRSL URP] No DMX light manager with a channel buffer in the scene.");
                return;
            }
            if (mgr.ChannelCount == 0)
            {
                Debug.LogError("[VRSL URP] The manager has no channel source publishing. Add a "
                             + "Synthetic DMX Channel Source (or another IVRSLDMXChannelSource) "
                             + "and set it to the Ramp pattern.");
                return;
            }

            // Finding a synthetic source in the scene does not make it the one that
            // filled the buffer. A second source, a disabled one, or any other
            // IVRSLDMXChannelSource the manager took instead would be compared against
            // a ramp it never published, and the result would read as a real pass or a
            // real failure. Only the manager knows who is publishing.
            var source = Object.FindFirstObjectByType<VRSL_SyntheticDMXChannelSource>();
            if (source == null || !ReferenceEquals(mgr.ChannelSource, source))
            {
                Debug.LogWarning("[VRSL URP] Validation compares against the synthetic source's Ramp "
                               + "pattern, and the manager is not publishing from one. Add a Synthetic "
                               + "DMX Channel Source and let it register before validating.");
                return;
            }
            if (source.pattern != VRSL_SyntheticDMXChannelSource.Pattern.Ramp)
            {
                Debug.LogWarning("[VRSL URP] Validation compares against the Ramp pattern. The active "
                               + $"source is in {source.pattern} mode, so there is nothing to compare to.");
                return;
            }
            // The manager holds the last value it was told for every slot, so a rotating
            // source fills the flat space over as many frames as it has universes. Run
            // before that and the universes still waiting read 0, which is a timing
            // artefact rather than a packing fault.
            //
            // Counted on the source rather than from Time.frameCount, which counts
            // frames since play began: a source enabled late, or one whose universe
            // count was raised at runtime, would pass that guard with the flat space
            // still half empty and fail validation as a packing fault.
            if (source.rotateUniverses && source.PublishedFrames < source.universes)
            {
                Debug.LogWarning("[VRSL URP] The source is rotating universes and has not published all "
                               + $"{source.universes} of them yet. Let it run a moment and validate again.");
                return;
            }

            var cs = mgr.computeShader;
            if (cs == null) { Debug.LogError("[VRSL URP] The manager has no compute shader assigned."); return; }

            int kernel;
            try { kernel = cs.FindKernel("ValidateChannels"); }
            catch (System.Exception)
            {
                Debug.LogError("[VRSL URP] The compute shader has no ValidateChannels kernel. "
                             + "It ships alongside UpdateLights in VRSLDMXLightUpdate.compute.");
                return;
            }

            int count = mgr.ChannelCount;
            var readback = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float));
            var values = new float[count];
            try
            {
                cs.SetBuffer(kernel, "_VRSLU_DMXChannels", mgr.ChannelBuffer);
                cs.SetInt("_VRSLU_DMXChannelCount", count);
                cs.SetBuffer(kernel, "_VRSLU_ValidationOut", readback);
                cs.SetInt("_VRSLU_ValidationStart", 1);      // DMX channel 1
                cs.SetInt("_VRSLU_ValidationCount", count);
                cs.Dispatch(kernel, Mathf.CeilToInt(count / 64f), 1, 1);
                readback.GetData(values);
            }
            finally
            {
                readback.Release();
            }

            int mismatches = 0;
            var first = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                byte expected = VRSL_SyntheticDMXChannelSource.RampValue(i);
                // The shader returns the byte scaled to 0..1, so the tolerance is
                // half a step: anything larger is a different channel, not rounding.
                int got = Mathf.RoundToInt(values[i] * 255f);
                if (got == expected) continue;
                mismatches++;
                if (mismatches <= MaxReport)
                    first.AppendLine($"  channel {i + 1}: expected {expected}, read {got}");
            }

            if (mismatches == 0)
            {
                Debug.Log($"[VRSL] PASS: all {count} channels read back through the shader match "
                        + "what the CPU published.");
                return;
            }

            Debug.LogError($"[VRSL] FAIL: {mismatches} of {count} channels differ.\n{first}"
                         + (mismatches > MaxReport ? $"  ... and {mismatches - MaxReport} more\n" : "")
                         + "A constant offset in the channel number is an indexing error; values that "
                         + "look like a neighbouring byte are a packing error. The last 8 addresses of "
                         + "each universe are padding and must read 0 — anything there means a block "
                         + "ran past slot 512, or the manager scattered one at the wrong stride.");
        }
    }
}
