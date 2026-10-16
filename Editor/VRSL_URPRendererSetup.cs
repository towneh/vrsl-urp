using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Editor utilities for the VRSL URP realtime light path. The package
    /// never reads, recommends, or mutates the URP asset or the URP renderer
    /// asset; those settings belong to the project. The single menu action
    /// exposed here is scene-level:
    /// <b>VRSL → URP → Add Light Manager to Active Scene</b> creates a
    /// <see cref="VRSL_DMXURPLightManager"/> in the active scene if missing and
    /// assigns any of its compute, lighting, volumetric, surface properties and
    /// light cull shaders left empty from the package. It only edits scene contents,
    /// never project assets.
    /// </summary>
    public static class VRSL_URPRendererSetup
    {
        const string MENU_SCENE = "VRSL/URP/Add Light Manager to Active Scene";

        const string LIGHTING_SHADER_NAME   = "Hidden/VRSL-URP/DeferredLighting";
        const string VOLUMETRIC_SHADER_NAME = "Hidden/VRSL-URP/VolumetricLighting";
        const string SURFACE_SHADER_NAME    = "Hidden/VRSL-URP/SurfaceProperties";
        const string COMPUTE_SHADER_FILTER  = "VRSLDMXLightUpdate t:ComputeShader";
        const string CULL_SHADER_FILTER     = "VRSLLightCull t:ComputeShader";

        // ── Scene-level: URP light manager GameObject ─────────────────────────

        [MenuItem(MENU_SCENE, false, 100)]
        public static void AddManagerToScene()
        {
            var report = new List<string>();

            var manager = Object.FindAnyObjectByType<VRSL_DMXURPLightManager>();
            if (manager == null)
            {
                var go = new GameObject("VRSL DMX URP Light Manager");
                Undo.RegisterCreatedObjectUndo(go, "Create VRSL DMX URP Light Manager");
                manager = Undo.AddComponent<VRSL_DMXURPLightManager>(go);
                EditorSceneManager.MarkSceneDirty(go.scene);
                report.Add("Created 'VRSL DMX URP Light Manager' in active scene.");
            }
            else
            {
                report.Add("Manager already in scene; refreshing shader references.");
            }

            bool assignedAny = false;

            if (manager.computeShader == null)
            {
                var compute = FindAsset<ComputeShader>(COMPUTE_SHADER_FILTER);
                if (compute != null)
                {
                    Undo.RecordObject(manager, "Assign VRSL Compute Shader");
                    manager.computeShader = compute;
                    assignedAny = true;
                    report.Add("Assigned compute shader.");
                }
            }

            if (manager.lightingShader == null)
            {
                var sh = Shader.Find(LIGHTING_SHADER_NAME);
                if (sh != null)
                {
                    Undo.RecordObject(manager, "Assign VRSL Lighting Shader");
                    manager.lightingShader = sh;
                    assignedAny = true;
                    report.Add("Assigned lighting shader.");
                }
            }

            if (manager.volumetricShader == null)
            {
                var sh = Shader.Find(VOLUMETRIC_SHADER_NAME);
                if (sh != null)
                {
                    Undo.RecordObject(manager, "Assign VRSL Volumetric Shader");
                    manager.volumetricShader = sh;
                    assignedAny = true;
                    report.Add("Assigned volumetric shader.");
                }
            }

            if (manager.surfacePropertiesShader == null)
            {
                var sh = Shader.Find(SURFACE_SHADER_NAME);
                if (sh != null)
                {
                    Undo.RecordObject(manager, "Assign VRSL Surface Properties Shader");
                    manager.surfacePropertiesShader = sh;
                    assignedAny = true;
                    report.Add("Assigned surface properties shader.");
                }
            }

            if (manager.lightCullShader == null)
            {
                var compute = FindAsset<ComputeShader>(CULL_SHADER_FILTER);
                if (compute != null)
                {
                    Undo.RecordObject(manager, "Assign VRSL Light Cull Shader");
                    manager.lightCullShader = compute;
                    assignedAny = true;
                    report.Add("Assigned light cull compute shader.");
                }
            }

            if (assignedAny) EditorUtility.SetDirty(manager);

            EditorUtility.DisplayDialog("VRSL URP Setup",
                "Done.\n\n• " + string.Join("\n• ", report) +
                "\n\nNext: set DMX Input on the manager to the grid layout your video uses, " +
                "or to Stream data if a channel source feeds the scene.", "OK");

            Selection.activeObject = manager.gameObject;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        static T FindAsset<T>(string filter) where T : Object
        {
            var guids = AssetDatabase.FindAssets(filter);
            foreach (var guid in guids)
            {
                var path  = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null) return asset;
            }
            return null;
        }
    }
}
