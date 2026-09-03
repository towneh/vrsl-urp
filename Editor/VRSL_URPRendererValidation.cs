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

            // One traversal for both checks below: each walks every MonoBehaviour in the
            // scene looking for fixtures, and a show scene is not a small one.
            var fixtureRenderers = FixtureRenderers();

            var used = ValidateCameras(urp, report);
            ReportNormalsSource(urp, report);
            problems += ValidateRenderers(urp, used, fixtureRenderers, report);
            ReportPrepassLayers(report);
            problems += ValidateSceneShaders(fixtureRenderers, report);
            return problems;
        }

        /// <summary>
        /// Where each camera's normals will come from, and why. "MSAA 4x, so VRSL draws
        /// its own" is an answer an author can act on; a saving that silently does or
        /// does not happen is not.
        /// </summary>
        static void ReportNormalsSource(UniversalRenderPipelineAsset urp, StringBuilder report)
        {
            // Which manager's settings apply is per camera, the way the runtime decides
            // it: the DMX manager draws the prepass for every camera it renders with
            // fixtures to light, and the AudioLink manager only where it does not.
            // Resolved here with the same camera filter, and with the scene's fixtures
            // standing in for a fixture count the manager only has in play mode.
            VRSL_URPLightManager dmx = null;
            foreach (var m in Object.FindObjectsByType<VRSL_URPLightManager>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (m != null && m.isActiveAndEnabled) { dmx = m; break; }
            VRSL_AudioLinkURPLightManager audioLink = null;
            foreach (var m in Object.FindObjectsByType<VRSL_AudioLinkURPLightManager>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (m != null && m.isActiveAndEnabled) { audioLink = m; break; }

            bool dmxHasFixtures = Application.isPlaying
                ? dmx != null && dmx.FixtureCount > 0
                : Object.FindObjectsByType<VRStageLighting_DMX_RealtimeLight>(
                      FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length > 0;

            report.AppendLine("Where VRSL's surface normals come from:");
            if (dmx == null && audioLink == null)
                report.AppendLine("      NOT CHECKED — no VRSL light manager switched on in the open "
                                + "scene. Below is what one would do on each camera, at its defaults.");

            int defaultIndex = -1;
            var assetSo = new SerializedObject(urp);
            var defaultProperty = assetSo.FindProperty("m_DefaultRendererIndex");
            if (defaultProperty != null) defaultIndex = defaultProperty.intValue;

            int seen = 0;
            foreach (var camera in Object.FindObjectsByType<Camera>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (camera == null || !camera.isActiveAndEnabled) continue;
                seen++;

                var dmxDecision = dmx != null
                    ? VRSLCameraFilter.Evaluate(camera, dmx.secondaryCameraMode, dmx.quality, null)
                    : VRSLCameraDecision.Skip;
                var audioLinkDecision = audioLink != null
                    ? VRSLCameraFilter.Evaluate(camera, audioLink.secondaryCameraMode, audioLink.quality, null)
                    : VRSLCameraDecision.Skip;
                bool dmxRenders       = dmxDecision.Render;
                bool audioLinkRenders = audioLinkDecision.Render;

                string who; bool forceOwn; LayerMask mask; string at;
                if (dmxRenders && dmxHasFixtures)
                {
                    who = dmx.name; forceOwn = dmx.forceOwnNormals; mask = dmx.prepassLayers;
                    at  = dmxDecision.ToString();
                }
                else if (audioLinkRenders)
                {
                    who = audioLink.name; forceOwn = audioLink.forceOwnNormals; mask = audioLink.prepassLayers;
                    at  = audioLinkDecision.ToString();
                }
                else if (dmx != null || audioLink != null)
                {
                    report.AppendLine($"      {camera.name}: VRSL draws no prepass here — "
                                    + (dmxRenders
                                        ? "the DMX manager has no fixtures to light and no "
                                          + "AudioLink manager renders this camera."
                                        : "every manager skips this camera (Secondary cameras)."));
                    continue;
                }
                else
                {
                    who = "no manager"; forceOwn = false; mask = ~0; at = null;
                }

                int index = defaultIndex;
                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                if (data != null)
                {
                    var property = new SerializedObject(data).FindProperty("m_RendererIndex");
                    if (property != null && property.intValue >= 0) index = property.intValue;
                }
                var rendererData = index >= 0 && index < urp.rendererDataList.Length
                                 ? urp.rendererDataList[index] as UniversalRendererData
                                 : null;
                int msaa = VRSLPrepassPolicy.PredictMsaa(camera, urp);
                var decision = VRSLPrepassPolicy.Decide(msaa, rendererData, forceOwn, mask);
                // The level beside the normals answer, because a mirror under the
                // Reduced policy renders at a level no inspector shows.
                report.AppendLine($"      {camera.name} ({who}"
                                + (at != null ? $", {at}" : "")
                                + $"): {decision.Reason}");
            }
            if (seen == 0)
                report.AppendLine("      No camera switched on in the open scene, so there is no "
                                + "camera to decide for.");
            if (dmx != null)
                report.AppendLine($"      {dmx.name}: " + VRSLCameraFilter.Describe(dmx.secondaryCameraMode, dmx.quality));
            if (audioLink != null)
                report.AppendLine($"      {audioLink.name}: " + VRSLCameraFilter.Describe(audioLink.secondaryCameraMode, audioLink.quality));
            report.AppendLine();
        }

        /// <summary>
        /// The package's own prepass layer mask, beside the renderer's. The renderer's
        /// decides what draws at all under priming; this one decides which surfaces
        /// light in their own colour, and a layer left out of it goes mid-grey with
        /// nothing in the frame to say why.
        /// </summary>
        static void ReportPrepassLayers(StringBuilder report)
        {
            report.AppendLine("Surfaces VRSL lights in their own colour:");
            int seen = 0;
            foreach (var manager in Object.FindObjectsByType<VRSL_URPLightManager>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (manager == null || !manager.isActiveAndEnabled) continue;
                seen++;
                VRSLDepthPolicyReport.PrepassLayersLine(
                    $"{manager.name} (DMX)", manager.prepassLayers, report);
            }
            foreach (var manager in Object.FindObjectsByType<VRSL_AudioLinkURPLightManager>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (manager == null || !manager.isActiveAndEnabled) continue;
                seen++;
                VRSLDepthPolicyReport.PrepassLayersLine(
                    $"{manager.name} (AudioLink)", manager.prepassLayers, report);
            }
            if (seen == 0)
                report.AppendLine("      NOT CHECKED — no VRSL light manager switched on in the "
                                + "open scene, so there is no prepass layer mask to read.");
            report.AppendLine();
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
        static HashSet<int> ValidateCameras(UniversalRenderPipelineAsset urp,
                                           StringBuilder report)
        {
            // -1 for "could not be read". Reporting renderer 1 instead would be a
            // guess presented in the same shape as an answer, on the one question this
            // block exists to settle.
            int defaultIndex = -1;
            var assetSo = new SerializedObject(urp);
            var defaultProperty = assetSo.FindProperty("m_DefaultRendererIndex");
            if (defaultProperty != null) defaultIndex = defaultProperty.intValue;

            // FindObjectsInactive.Exclude drops cameras on inactive GameObjects and
            // nothing else, so a disabled Camera component on an active object survives
            // it. That camera renders through nothing, and letting it through would put
            // its renderer into `used` and silence the "no enabled camera picks this one"
            // line below on a renderer that genuinely nothing selects.
            var found = Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var cameras = new List<Camera>();
            int switchedOff = 0;
            foreach (var candidate in found)
            {
                if (candidate == null) continue;
                if (candidate.isActiveAndEnabled) cameras.Add(candidate);
                else switchedOff++;
            }

            report.AppendLine("Cameras in this scene:");
            if (cameras.Count == 0)
            {
                report.AppendLine("      NOT CHECKED — the scene has no camera switched on, "
                                + "so which renderer applies could not be worked out. "
                                + (switchedOff > 0
                                    ? $"There {(switchedOff == 1 ? "is" : "are")} "
                                    + $"{switchedOff} switched off. Switch one on, or open a "
                                    + "scene with your own camera in it, for this to mean "
                                    + "anything."
                                    : "Open a scene with your own camera in it for this to "
                                    + "mean anything."));
                report.AppendLine();
                return new HashSet<int>();
            }

            var used = new HashSet<int>();
            foreach (var camera in cameras)
            {
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

                if (index < 0)
                {
                    report.AppendLine($"      {camera.name}: NOT CHECKED — the renderer "
                                    + "index could not be read from the pipeline asset, so "
                                    + "which renderer this camera uses is unknown.");
                    continue;
                }

                used.Add(index);
                string name = index < urp.rendererDataList.Length
                            && urp.rendererDataList[index] != null
                            ? urp.rendererDataList[index].name
                            : "missing";
                report.AppendLine($"      {camera.name} renders through renderer {index + 1} "
                                + $"({name}), {how}.");
            }

            // Said rather than left out. A camera missing from a block headed "Cameras in
            // this scene" is a puzzle; one line naming why it is absent is not.
            if (switchedOff > 0)
                report.AppendLine($"      {switchedOff} more "
                                + $"{(switchedOff == 1 ? "camera is" : "cameras are")} "
                                + "switched off and not counted here. A camera that is off "
                                + "renders through no renderer.");

            if (used.Count > 1)
                report.AppendLine("      These cameras do not share a renderer, so the settings "
                                + "below apply to different ones. Read each against the cameras "
                                + "that use it.");

            report.AppendLine();
            return used;
        }

        /// <param name="used">Renderer indices the open scene's enabled cameras resolve
        /// to, empty where that could not be worked out. Used to annotate, not to scope:
        /// a mirror or portal camera created at runtime is in no scene at edit time, and
        /// a renderer nothing here selects is still the one it will pick.</param>
        static int ValidateRenderers(UniversalRenderPipelineAsset urp, HashSet<int> used,
                                     HashSet<Renderer> fixtureRenderers, StringBuilder report)
        {
            int problems = 0;
            int msaaSamples = urp.msaaSampleCount;
            var fixtureLayers = FixtureLayers(fixtureRenderers);

            int index = 0;
            bool sawAny = false;
            foreach (var data in urp.rendererDataList)
            {
                index++;
                if (data == null) continue;
                sawAny = true;

                report.AppendLine($"Renderer {index}: {data.name}");
                if (used.Count > 0 && !used.Contains(index - 1))
                    report.AppendLine("      No enabled camera in the open scene renders "
                                    + "through this one. Still worth reading: a mirror or a "
                                    + "portal camera made at runtime is in no scene now, and "
                                    + "it picks a renderer the same way.");

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

                // Auto is not Forced. URP primes under Auto only when something else in
                // the frame already requires a depth prepass, so a layer-mask mismatch
                // there is a thing that may bite rather than one that certainly does, and
                // reporting it as a failure puts a red line against a scene that renders.
                bool priming  = universal.depthPrimingMode == DepthPrimingMode.Forced;
                bool mayPrime = universal.depthPrimingMode == DepthPrimingMode.Auto;
                if (priming || mayPrime)
                    report.AppendLine("      With priming on, an opaque shader whose depth pass "
                                    + "draws different geometry from its forward pass is "
                                    + "dropped from the frame — as is one with no depth pass "
                                    + "for the prepass that ran. Every shader this package "
                                    + "ships reproduces its forward vertex stage in both its "
                                    + "depth passes; a custom fixture shader here has to too.");

                // Two facts side by side leave the reader to know the interaction, and the
                // interaction is not fixed: URP's own predicate declines to prime on a
                // multisampled target, and a project is free to ship a URP without that
                // condition. Basis does. So the honest report names both readings and says
                // which one applies is a property of the URP installed here, rather than
                // asserting a behaviour that depends on somebody else's source.
                if ((priming || mayPrime) && msaaSamples > 1)
                    report.AppendLine($"      MSAA is {msaaSamples}x, and whether priming runs "
                                    + "at all then depends on the URP this project resolves. "
                                    + "Stock URP declines to prime on a multisampled target, "
                                    + "so priming would be inert and these settings would "
                                    + "render as though it were Disabled. A project shipping "
                                    + "its own URP may have removed that condition, and then "
                                    + "priming really is running here. Read "
                                    + "UniversalRendererRenderGraph if it matters which — a "
                                    + "URP under Packages/ wins over the registry one.");

                problems += ValidateLayerMask(universal, fixtureLayers, priming, mayPrime, report);
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
                                     bool priming, bool mayPrime, StringBuilder report)
        {
            // Read through SerializedObject: the opaque layer mask is not public API on
            // every URP version, and a missing field should report rather than throw.
            // Null carries "could not be read" through to the verdict, which treats it as
            // unchecked rather than as any particular mask.
            var so = new SerializedObject(universal);
            var maskProperty = so.FindProperty("m_OpaqueLayerMask");
            int? mask = maskProperty != null ? maskProperty.intValue : (int?)null;

            return VRSLDepthPolicyReport.LayerMaskVerdict(
                mask, fixtureLayers, priming, mayPrime, report);
        }

        /// <summary>
        /// Shaders in the scene that render opaque and are missing a depth pass.
        ///
        /// The opaque range is where the prepass runs, so a shader below 2500 without
        /// both passes is the failure this milestone is about. Above it, the prepass
        /// never invokes them and their absence means nothing.
        ///
        /// A shader qualifies by being one this package ships, or by being on a renderer
        /// under a VRSL fixture. The second case is the one worth having: a fixture whose
        /// mesh is drawn by a custom shader disappears under priming exactly as a package
        /// one would, and the package cannot fix that shader but can say which it is.
        /// Everything else in the scene is left alone — an avatar with a broken depth pass
        /// is a real fault, but it is not this menu item's to report.
        /// </summary>
        static int ValidateSceneShaders(HashSet<Renderer> onFixtures, StringBuilder report)
        {
            const int OpaqueEnd = 2500;
            var seen    = new HashSet<Shader>();
            var missing = new List<string>();
            int examined = 0;

            foreach (var renderer in Object.FindObjectsByType<Renderer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (renderer == null) continue;
                bool onFixture = onFixtures.Contains(renderer);
                foreach (var material in renderer.sharedMaterials)
                {
                    var shader = material != null ? material.shader : null;
                    if (shader == null) continue;
                    bool ours = IsPackageShader(shader.name);
                    if (!ours && !onFixture) continue;
                    // -1 means the material takes the shader's queue rather than
                    // overriding it, and comparing that against the opaque ceiling lets
                    // every transparent shader through and reports the queue as -1.
                    int queue = material.renderQueue >= 0 ? material.renderQueue
                                                          : shader.renderQueue;
                    if (queue > OpaqueEnd) continue;
                    // Counted after the filters, not before. Adding every shader in the
                    // scene to the set would make the pass line below quote a number that
                    // has nothing to do with what was examined.
                    if (!seen.Add(shader)) continue;
                    examined++;

                    bool depthOnly    = HasPass(shader, "DepthOnly");
                    // Either tag satisfies the depth-normals prepass: URP draws it with
                    // both, and one of its branches draws DepthNormalsOnly alone. This
                    // package's own VRSLSurfacePrepass matches the same pair.
                    bool depthNormals = HasPass(shader, "DepthNormals")
                                     || HasPass(shader, "DepthNormalsOnly");
                    if (depthOnly && depthNormals) continue;

                    string absent = !depthOnly && !depthNormals ? "DepthOnly and DepthNormals"
                                  : !depthOnly                  ? "DepthOnly"
                                                                : "DepthNormals";
                    missing.Add($"{shader.name} (queue {queue}, no {absent}), "
                              + (ours ? "shipped with this package"
                                      : "on a VRSL fixture in this scene"));
                }
            }

            report.AppendLine("Shaders in this scene:");
            if (examined == 0)
            {
                report.AppendLine("      NOT CHECKED — nothing here draws opaque geometry with "
                                + "a VRSL shader, and no VRSL fixture carries a custom one.");
                return 0;
            }
            if (missing.Count == 0)
            {
                report.AppendLine($"      All {examined} shader(s) examined draw opaque geometry "
                                + "and have both depth passes.");
                return 0;
            }

            foreach (string entry in missing)
                report.AppendLine($"FAIL  {entry}. It draws in the opaque range, so under depth "
                                + "priming it can be culled from the frame. Which prepass "
                                + "priming tests against depends on whether anything in the "
                                + "frame asks URP for a normals texture, so both passes are "
                                + "needed to be safe in any project.");
            // Said once, at the end, because the alternative is somebody switching the
            // object off, running this again, seeing the same line and concluding the
            // command is stale. Switched-off objects are counted on purpose: one gets
            // switched back on, and a report that went quiet would have hidden the fault
            // rather than fixed it.
            report.AppendLine("      Objects switched off in the scene are included above. "
                            + "Disabling one will not clear its line — the shader is still "
                            + "in this scene and still draws the moment it comes back.");
            return missing.Count;
        }

        /// <summary>
        /// Whether this package ships the shader, by name.
        ///
        /// The shipped names are not all one shape — <c>VRSL-URP/...</c> for the fixture
        /// and projection shaders, <c>Hidden/VRSL-URP/...</c> for the passes, and one
        /// <c>AudioLink/VRSL-URP Linear Interpolator</c> — so the distinctive part is the
        /// token rather than any prefix. Erring wide is the safe direction here: a false
        /// match only means a shader gets checked and its finding labelled ours, while a
        /// false miss skips one of our own shaders that is not on a fixture.
        /// </summary>
        static bool IsPackageShader(string name) => name.Contains("VRSL-URP");

        /// <summary>Renderers below a VRSL fixture in the open scene.</summary>
        static HashSet<Renderer> FixtureRenderers()
        {
            var found = new HashSet<Renderer>();
            foreach (var fixture in Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (fixture == null) continue;
                if (!fixture.GetType().Name.StartsWith("VRStageLighting")) continue;
                foreach (var renderer in fixture.GetComponentsInChildren<Renderer>(true))
                    if (renderer != null) found.Add(renderer);
            }
            return found;
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
        /// <summary>Layers occupied by VRSL fixtures in the open scene.</summary>
        static HashSet<int> FixtureLayers(HashSet<Renderer> fixtureRenderers)
        {
            var layers = new HashSet<int>();
            foreach (var renderer in fixtureRenderers)
                if (renderer != null) layers.Add(renderer.gameObject.layer);
            return layers;
        }
    }
}
