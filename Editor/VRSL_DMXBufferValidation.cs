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
    /// Menu: VRSL → URP → Validate DMX Channel Buffer. Play mode only, since the
    /// buffer is uploaded per frame by the manager.
    /// </summary>
    public static class VRSL_DMXBufferValidation
    {
        const int MaxReport = 8;

        [MenuItem("VRSL/URP/Validate DMX Channel Buffer", false, 400)]
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

            var source = Object.FindFirstObjectByType<VRSL_SyntheticDMXChannelSource>();
            if (source == null || source.pattern != VRSL_SyntheticDMXChannelSource.Pattern.Ramp)
            {
                Debug.LogWarning("[VRSL URP] Validation compares against the synthetic source's Ramp "
                               + "pattern. Without it in Ramp mode there is nothing to compare to.");
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
                         + "look like a neighbouring byte are a packing error.");
        }
    }
}
