using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Checks the active renderer against what the package needs from it, and says
    /// what an author will see if it does not hold.
    ///
    /// The setting that matters is depth priming. With it on, URP renders a depth
    /// prepass and then draws opaque geometry with an <c>Equal</c> depth test, so any
    /// opaque shader whose depth pass does not reproduce its forward pass exactly is
    /// culled from the frame entirely. A fixture body that vanishes reads as a culling
    /// or LOD fault rather than a depth one, which is why it is worth a command that
    /// says so outright.
    /// </summary>
    public static class VRSL_URPRendererValidation
    {
        const string Menu = "VRSL/URP/Validate Renderer Setup";

        [MenuItem(Menu, false, 102)]
        public static void ValidateFromMenu()
        {
            var report = new StringBuilder();
            int problems = Validate(report);

            string summary = problems == 0
                ? "Nothing to fix. The renderer gives VRSL what it needs."
                : $"{problems} thing(s) worth looking at. Detail in the Console.";

            if (problems > 0) Debug.LogWarning("[VRSL] Renderer setup\n" + report);
            else              Debug.Log("[VRSL] Renderer setup\n" + report);

            EditorUtility.DisplayDialog("VRSL Renderer Setup", summary, "OK");
        }

        /// <summary>Appends findings and returns how many need attention.</summary>
        public static int Validate(StringBuilder report)
        {
            int problems = 0;

            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset urp)
            {
                report.AppendLine("FAIL  This project is not running the Universal Render "
                                + "Pipeline, so none of the VRSL URP path will render.");
                return 1;
            }

            report.AppendLine($"Pipeline asset: {urp.name}");
            report.AppendLine($"MSAA: {urp.msaaSampleCount}x");
            report.AppendLine($"Depth texture: {(urp.supportsCameraDepthTexture ? "on" : "off")} "
                            + "on the asset. VRSL asks the pipeline for depth per pass, so it "
                            + "does not depend on this being ticked — and it needs no scene "
                            + "light of any kind to get it.");
            report.AppendLine();

            problems += ValidateRenderers(urp, report);
            problems += ValidateSceneShaders(report);
            return problems;
        }

        static int ValidateRenderers(UniversalRenderPipelineAsset urp, StringBuilder report)
        {
            int problems = 0;
            var fixtureLayers = FixtureLayers();

            int index = 0;
            bool sawAny = false;
            foreach (var data in urp.rendererDataList)
            {
                index++;
                if (data == null) continue;
                sawAny = true;

                report.AppendLine($"Renderer {index}: {data.name}");

                if (data is not UniversalRendererData universal)
                {
                    report.AppendLine("      Not a Universal Renderer, so its depth priming "
                                    + "and prepass settings could not be read. If VRSL "
                                    + "fixtures render through it, check those by hand.");
                    report.AppendLine();
                    continue;
                }

                report.AppendLine($"      Rendering mode: {universal.renderingMode}");
                report.AppendLine($"      Depth priming:  {universal.depthPrimingMode}");

                bool priming = universal.depthPrimingMode != DepthPrimingMode.Disabled;
                if (priming)
                    report.AppendLine("      With priming on, an opaque shader whose depth pass "
                                    + "draws different geometry from its forward pass is "
                                    + "dropped from the frame. Every shader this package ships "
                                    + "reproduces its forward vertex stage in its depth passes; "
                                    + "a custom fixture shader in this scene has to as well.");

                problems += ValidateLayerMask(universal, fixtureLayers, priming, report);
                report.AppendLine();
            }

            if (!sawAny)
            {
                report.AppendLine("FAIL  Could not read any renderer from the pipeline asset, so "
                                + "nothing here was checked.");
                problems++;
            }
            return problems;
        }

        /// <summary>
        /// A fixture on a layer the prepass excludes, with priming on, is a fixture that
        /// does not draw at all — the prepass never writes its depth, so the Equal test
        /// in the forward pass rejects every one of its fragments.
        /// </summary>
        static int ValidateLayerMask(UniversalRendererData universal, HashSet<int> fixtureLayers,
                                     bool priming, StringBuilder report)
        {
            if (fixtureLayers.Count == 0)
            {
                report.AppendLine("      No VRSL fixtures in the open scene, so the prepass "
                                + "layer mask was not checked against anything.");
                return 0;
            }

            // Read through SerializedObject: the opaque layer mask is not public API on
            // every URP version, and a missing field should report rather than throw.
            var so = new SerializedObject(universal);
            var maskProperty = so.FindProperty("m_OpaqueLayerMask");
            if (maskProperty == null)
            {
                report.AppendLine("      Could not read this renderer's opaque layer mask, so "
                                + "it was not checked. Confirm by hand that it includes the "
                                + "layers your fixtures sit on.");
                return 0;
            }

            int mask = maskProperty.intValue;
            var excluded = new List<string>();
            foreach (int layer in fixtureLayers)
                if ((mask & (1 << layer)) == 0)
                    excluded.Add($"{LayerMask.LayerToName(layer)} ({layer})");

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

            report.AppendLine($"      Fixtures are on {layers}, which the opaque layer mask "
                            + "excludes. Harmless while depth priming is off, and those "
                            + "fixtures disappear the moment it is turned on.");
            return 0;
        }

        /// <summary>
        /// Package shaders in the scene that render opaque and are missing a depth pass.
        ///
        /// The opaque range is where the prepass runs, so a shader below 2500 without
        /// both passes is the failure this milestone is about. Above it, the prepass
        /// never invokes them and their absence means nothing.
        /// </summary>
        static int ValidateSceneShaders(StringBuilder report)
        {
            const int OpaqueEnd = 2500;
            var checkedAlready = new HashSet<Shader>();
            var missing = new List<string>();

            foreach (var renderer in Object.FindObjectsByType<Renderer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (renderer == null) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    var shader = material != null ? material.shader : null;
                    if (shader == null || !checkedAlready.Add(shader)) continue;
                    if (!shader.name.StartsWith("VRSL")) continue;
                    if (material.renderQueue > OpaqueEnd) continue;

                    bool depthOnly    = HasPass(shader, "DepthOnly");
                    bool depthNormals = HasPass(shader, "DepthNormals");
                    if (depthOnly && depthNormals) continue;

                    string absent = !depthOnly && !depthNormals ? "DepthOnly and DepthNormals"
                                  : !depthOnly                  ? "DepthOnly"
                                                                : "DepthNormals";
                    missing.Add($"{shader.name} (queue {material.renderQueue}, no {absent})");
                }
            }

            report.AppendLine("Shaders in this scene:");
            if (missing.Count == 0)
            {
                report.AppendLine("      Every VRSL shader drawing opaque geometry has both "
                                + "depth passes.");
                return 0;
            }

            foreach (string entry in missing)
                report.AppendLine($"FAIL  {entry}. It draws in the opaque range, so under depth "
                                + "priming it will be culled from the frame.");
            return missing.Count;
        }

        /// <summary>
        /// Whether a shader declares a pass with the given <c>LightMode</c>.
        ///
        /// <c>FindPassTagValue</c> reads the tag off each sub-shader pass without
        /// depending on renderer internals, which is what makes this survive a URP
        /// version bump.
        /// </summary>
        static bool HasPass(Shader shader, string lightMode)
        {
            var tag = new ShaderTagId("LightMode");
            var wanted = new ShaderTagId(lightMode);
            for (int sub = 0; sub < shader.subshaderCount; sub++)
                for (int pass = 0; pass < shader.GetPassCountInSubshader(sub); pass++)
                    if (shader.FindPassTagValue(sub, pass, tag) == wanted)
                        return true;
            return false;
        }

        /// <summary>Layers occupied by VRSL fixtures in the open scene.</summary>
        static HashSet<int> FixtureLayers()
        {
            var layers = new HashSet<int>();
            foreach (var fixture in Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (fixture == null) continue;
                string type = fixture.GetType().Name;
                if (!type.StartsWith("VRStageLighting")) continue;
                foreach (var renderer in fixture.GetComponentsInChildren<Renderer>(true))
                    if (renderer != null) layers.Add(renderer.gameObject.layer);
            }
            return layers;
        }
    }
}
