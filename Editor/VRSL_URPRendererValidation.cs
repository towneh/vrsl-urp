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

            problems += ValidateCameras(urp, report);
            problems += ValidateRenderers(urp, report);
            problems += ValidateSceneShaders(report);
            return problems;
        }

        /// <summary>
        /// Which renderer each camera in the scene actually renders through.
        ///
        /// A pipeline asset can carry several renderers with different settings — Basis
        /// ships priming Forced on its desktop renderer and Disabled on its camera one —
        /// and a camera picks exactly one. Listing the renderers without saying which
        /// applies leaves an author to guess, and a camera that overrides nothing takes
        /// the asset's default silently, so the answer is not visible on the camera
        /// either.
        /// </summary>
        static int ValidateCameras(UniversalRenderPipelineAsset urp, StringBuilder report)
        {
            int defaultIndex = 0;
            var assetSo = new SerializedObject(urp);
            var defaultProperty = assetSo.FindProperty("m_DefaultRendererIndex");
            if (defaultProperty != null) defaultIndex = defaultProperty.intValue;

            var cameras = Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            report.AppendLine("Cameras in this scene:");
            if (cameras.Length == 0)
            {
                report.AppendLine("      NOT CHECKED — the scene has no enabled camera, so "
                                + "which renderer applies could not be worked out. Open a "
                                + "scene with your own camera in it for this to mean anything.");
                report.AppendLine();
                return 0;
            }

            var used = new HashSet<int>();
            foreach (var camera in cameras)
            {
                if (camera == null) continue;
                int index = defaultIndex;
                string how = "the asset's default";

                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                if (data != null)
                {
                    var so = new SerializedObject(data);
                    var property = so.FindProperty("m_RendererIndex");
                    if (property != null && property.intValue >= 0)
                    {
                        index = property.intValue;
                        how = "set on the camera";
                    }
                }

                used.Add(index);
                string name = index >= 0 && index < urp.rendererDataList.Length
                            && urp.rendererDataList[index] != null
                            ? urp.rendererDataList[index].name
                            : "missing";
                report.AppendLine($"      {camera.name} renders through renderer {index + 1} "
                                + $"({name}), {how}.");
            }

            if (used.Count > 1)
                report.AppendLine("      These cameras do not share a renderer, so the settings "
                                + "below apply to different ones. Read each against the cameras "
                                + "that use it.");

            report.AppendLine();
            return 0;
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
            var so0 = new SerializedObject(universal);
            var mask0 = so0.FindProperty("m_OpaqueLayerMask");
            if (fixtureLayers.Count == 0)
            {
                // Reported rather than passed over. "Nothing to check" and "checked and
                // fine" are different answers and only one of them is reassurance.
                report.AppendLine("      NOT CHECKED — no VRSL fixtures in the open scene. "
                                + "Opaque layer mask is "
                                + (mask0 != null ? DescribeMask(mask0.intValue) : "unreadable")
                                + ". A fixture on a layer outside that, with priming on, does "
                                + "not draw at all.");
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
            if (checkedAlready.Count == 0)
            {
                report.AppendLine("      NOT CHECKED — no VRSL shaders in the open scene.");
                return 0;
            }
            if (missing.Count == 0)
            {
                report.AppendLine($"      All {checkedAlready.Count} VRSL shader(s) here that "
                                + "draw opaque geometry have both depth passes.");
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

        /// <summary>The layers a mask covers, named rather than as a bit pattern.</summary>
        static string DescribeMask(int mask)
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
