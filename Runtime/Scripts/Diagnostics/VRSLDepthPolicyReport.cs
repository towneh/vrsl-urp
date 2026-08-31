#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// The prepass layer-mask verdict, separated from reading it off a renderer.
    ///
    /// This is the finding `VRSL → URP → Validate Renderer Setup` exists to produce: a
    /// fixture sitting on a layer the depth prepass excludes, with priming on, does not
    /// draw at all — and the symptom is a fixture that is simply absent, which reads as
    /// anything but a layer mask.
    ///
    /// It lives here, in the runtime assembly under a UNITY_EDITOR guard, rather than
    /// beside the menu item in Editor/, for one reason: the package's test assembly does
    /// not reference the editor assembly, and the suite runs PlayMode only. A row written
    /// against an editor-only assembly would need a second test assembly and a second
    /// runner invocation, and the runner script is not committed — so that row would
    /// quietly never run, which is the failure mode this milestone has already been bitten
    /// by once. Same shape and same reason as VRSLWiring.
    ///
    /// Reading the mask stays in the editor code, because it needs SerializedObject. What
    /// is here is the decision, which is a pure function of the mask and the layers.
    /// </summary>
    internal static class VRSLDepthPolicyReport
    {
        /// <summary>
        /// Appends the layer-mask verdict and returns how many findings need attention.
        /// </summary>
        /// <param name="mask">
        /// The renderer's opaque layer mask, or null where it could not be read. Null is
        /// a distinct answer from any mask value: it means unchecked, and reporting it as
        /// a pass would be reassurance nobody earned.
        /// </param>
        /// <param name="fixtureLayers">Layers occupied by VRSL fixtures in the open scene.</param>
        /// <param name="priming">Depth priming is Forced on this renderer.</param>
        /// <param name="mayPrime">Depth priming is Auto on this renderer.</param>
        internal static int LayerMaskVerdict(int? mask, ICollection<int> fixtureLayers,
                                             bool priming, bool mayPrime, StringBuilder report)
        {
            if (fixtureLayers == null || fixtureLayers.Count == 0)
            {
                // Reported rather than passed over. "Nothing to check" and "checked and
                // fine" are different answers and only one of them is reassurance.
                report.AppendLine("      NOT CHECKED — no VRSL fixtures in the open scene. "
                                + "Opaque layer mask is "
                                + (mask.HasValue ? DescribeMask(mask.Value) : "unreadable")
                                + ". A fixture on a layer outside that, with priming on, does "
                                + "not draw at all.");
                return 0;
            }

            if (!mask.HasValue)
            {
                report.AppendLine("      Could not read this renderer's opaque layer mask, so "
                                + "it was not checked. Confirm by hand that it includes the "
                                + "layers your fixtures sit on.");
                return 0;
            }

            var excluded = new List<string>();
            foreach (int layer in fixtureLayers)
                if ((mask.Value & (1 << layer)) == 0)
                {
                    string name = LayerMask.LayerToName(layer);
                    excluded.Add(string.IsNullOrEmpty(name) ? $"layer {layer}"
                                                            : $"{name} ({layer})");
                }

            if (excluded.Count == 0)
            {
                report.AppendLine("      Opaque layer mask covers every layer the scene's "
                                + "fixtures are on.");
                return 0;
            }

            string layers = string.Join(", ", excluded);
            if (priming)
            {
                report.AppendLine($"FAIL  Fixtures are on {layers}, which this renderer's opaque "
                                + "layer mask excludes, and depth priming is on. Those fixtures "
                                + "will not draw at all. Add the layer to the mask, or turn "
                                + "depth priming off.");
                return 1;
            }

            if (mayPrime)
            {
                report.AppendLine($"      Fixtures are on {layers}, which this renderer's "
                                + "opaque layer mask excludes, and depth priming is on Auto. "
                                + "Auto primes only when something else in the frame already "
                                + "needs a depth prepass, so those fixtures draw until "
                                + "something asks for one and then stop. Add the layer to the "
                                + "mask, or set priming to Disabled to settle it either way.");
                return 0;
            }

            report.AppendLine($"      Fixtures are on {layers}, which the opaque layer mask "
                            + "excludes. Harmless while depth priming is off, and those "
                            + "fixtures disappear the moment it is turned on.");
            return 0;
        }

        /// <summary>Names the layers a mask includes, for a report a person reads.</summary>
        internal static string DescribeMask(int mask)
        {
            if (mask == ~0) return "everything";
            var named = new List<string>();
            for (int layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) == 0) continue;
                string name = LayerMask.LayerToName(layer);
                named.Add(string.IsNullOrEmpty(name) ? layer.ToString() : name);
            }
            return named.Count == 0 ? "nothing" : string.Join(", ", named);
        }
    }
}
#endif
