using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRSL.URP.Profiling.Editor
{
    /// <summary>
    /// Builds (or rebuilds) a deterministic VRSL profiling scene with N matched
    /// fixtures arranged on a horizontal truss aimed at a flat floor, a fixed-pose
    /// camera, and a CRT-bypass synthetic DMX source. Drives the fixture-count
    /// and camera sweeps documented in the 2.9.0 frametime test plan without
    /// needing to re-author scenes by hand for every cell of the table.
    /// </summary>
    public class VRSLProfilingSceneBuilder : EditorWindow
    {
        public enum LightingPath
        {
            URPRealtime,
            LegacyMeshShader,
        }

        public enum CameraVariant
        {
            InsideCones,
            OutsideCones,
        }

        public enum FixtureType
        {
            MoverSpotlight,
            MoverWashlight,
            StaticParLight,
            StaticBlinder,
        }

        const string PkgRoot         = "Packages/town.mr.vrsl-urp/";
        const string URPManager      = PkgRoot + "Runtime/Prefabs/DMX/Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-URP-LightManager-Horizontal.prefab";
        const string DepthLightPath  = PkgRoot + "Runtime/Prefabs/Directional Light (For Depth).prefab";

        // Per-(path, kind) prefab paths. The Horizontal-mode 13-channel variants
        // are used for both URP and legacy paths since the profiling scene only
        // exercises rendering cost; the DMX channel mapping is irrelevant once
        // the synthetic source is feeding pre-decoded values.
        static string PrefabPath(LightingPath p, FixtureType t)
        {
            const string urpDir    = PkgRoot + "Runtime/Prefabs/DMX/Horizontal Mode/DMX-13CH-URP-Fixtures/";
            const string legacyDir = PkgRoot + "Runtime/Prefabs/DMX/Horizontal Mode/";
            bool urp = p == LightingPath.URPRealtime;
            string dir    = urp ? urpDir : legacyDir;
            string suffix = urp ? "-URP.prefab" : ".prefab";
            switch (t)
            {
                case FixtureType.MoverWashlight: return dir + "VRSL-DMX-Mover-WashLight-H-13CH" + suffix;
                case FixtureType.MoverSpotlight: return dir + "VRSL-DMX-Mover-Spotlight-H-13CH" + suffix;
                case FixtureType.StaticParLight: return dir + "VRSL-DMX-Static-ParLight-H-13CH" + suffix;
                case FixtureType.StaticBlinder:  return dir + "VRSL-DMX-Static-Blinder-H-13CH"  + suffix;
                default:                         return null;
            }
        }

        const string RootName       = "VRSL Profiling Root";
        const string TrussName      = "Truss";
        const string CameraName     = "Profiling Camera";
        const string FloorName      = "Floor";
        const string ManagerName    = "VRSL URP Light Manager";
        const string SourceName     = "Synthetic DMX Source";
        const string DepthLightName = "Directional Light (For Depth)";

        LightingPath  path           = LightingPath.URPRealtime;
        FixtureType   fixtureType    = FixtureType.MoverSpotlight;
        int           fixtureCount   = 50;
        CameraVariant cameraVariant  = CameraVariant.InsideCones;
        float         trussWidth     = 30f;
        float         trussHeight    = 6f;
        float         floorSize      = 150f;
        float         fixtureTilt    = 90f;

        [MenuItem("VRSL/Profiling/Build Profiling Scene")]
        public static void ShowWindow() => GetWindow<VRSLProfilingSceneBuilder>("Profiling Scene Builder");

        void OnGUI()
        {
            EditorGUILayout.LabelField("Profiling Scene Builder", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Builds a deterministic profiling scene matching the 2.9.0 frametime test " +
                "plan. Re-running with a new fixture count rebuilds the truss in place — the " +
                "camera, floor, manager, and synthetic DMX source are preserved.",
                MessageType.Info);

            EditorGUILayout.Space();
#if !VRSL_URP
            // URP is not installed in this project; URPRealtime can't run, so
            // pin the path to LegacyMeshShader and gray the dropdown entry out
            // so the user sees the option exists but is unavailable.
            if (path == LightingPath.URPRealtime) path = LightingPath.LegacyMeshShader;
#endif
            path = (LightingPath) EditorGUILayout.EnumPopup(
                new GUIContent("Lighting path"),
                path,
                e =>
                {
#if !VRSL_URP
                    if ((LightingPath)e == LightingPath.URPRealtime) return false;
#endif
                    return true;
                },
                includeObsolete: false);
            fixtureType    = (FixtureType)  EditorGUILayout.EnumPopup("Fixture type",    fixtureType);
            fixtureCount   = EditorGUILayout.IntSlider("Fixture count",   fixtureCount,  1, 256);
            cameraVariant  = (CameraVariant)EditorGUILayout.EnumPopup("Camera variant", cameraVariant);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Layout", EditorStyles.boldLabel);
            trussWidth     = EditorGUILayout.FloatField("Truss width (m)",   trussWidth);
            trussHeight    = EditorGUILayout.FloatField("Truss height (m)",  trussHeight);
            floorSize      = EditorGUILayout.FloatField("Floor size (m)",    floorSize);
            fixtureTilt    = EditorGUILayout.Slider("Fixture aim tilt (deg)", fixtureTilt, 0f, 90f);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Build / Rebuild Profiling Scene", GUILayout.Height(36)))
                    Build();
            }
        }

        void Build()
        {
            var scene = EditorSceneManager.GetActiveScene();

            var root = GameObject.Find(RootName);
            if (root == null)
            {
                root = new GameObject(RootName);
                Undo.RegisterCreatedObjectUndo(root, "Create profiling root");
            }

            StripDirectionalLights();
            EnsureDepthLight(root);
            EnsureFloor(root);
            EnsureSource(root);
            EnsureManager(root);
            RebuildTruss(root);
            EnsureCamera(root);

            // If the scene is in play mode, force the synthetic source to re-publish
            // globals and rebind to the (possibly newly-spawned) URP manager.
            if (Application.isPlaying)
            {
                var src = root.GetComponentInChildren<VRSLProfilingSyntheticDMXSource>();
                src?.Rebind();
            }

            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log($"[VRSL Profiling] Built {path} scene with {fixtureCount} {fixtureType} fixtures, camera={cameraVariant}.");
        }

        // Removes scene directional lights so profile numbers reflect VRSL-only
        // cost. Unity adds a default Directional Light to every new scene; left
        // in place it adds an unrelated shadow/lighting term that pollutes the
        // absolute numbers (it cancels in the legacy-vs-URP delta, but not in
        // the per-N-fixture headline). EnsureDepthLight then re-adds VRSL's own
        // depth-only directional for the legacy path.
        static void StripDirectionalLights()
        {
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (light != null && light.type == LightType.Directional)
                    Undo.DestroyObjectImmediate(light.gameObject);
            }
        }

        // The legacy mesh-shader fixture path samples _CameraDepthTexture in its
        // volumetric/projection passes. In Built-in RP that texture is only
        // populated when a directional light is shadow-casting in the scene, so
        // VRSL ships a "Directional Light (For Depth)" prefab that exists purely
        // to trigger depth-texture generation. The URPRealtime path doesn't
        // need it (URP populates depth via its renderer regardless), so we add
        // it only for the legacy path and tear it down again if the user
        // switches back to URPRealtime.
        void EnsureDepthLight(GameObject root)
        {
            var existing = root.transform.Find(DepthLightName)?.gameObject;

            if (path != LightingPath.LegacyMeshShader)
            {
                if (existing != null)
                    Undo.DestroyObjectImmediate(existing);
                return;
            }

            if (existing != null) return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DepthLightPath);
            if (prefab == null)
            {
                Debug.LogError($"[VRSL Profiling] Depth-light prefab not found: {DepthLightPath}");
                return;
            }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            inst.name = DepthLightName;
            Undo.RegisterCreatedObjectUndo(inst, "Spawn directional depth light");
        }

        void EnsureFloor(GameObject root)
        {
            var floor = root.transform.Find(FloorName)?.gameObject
                     ?? GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = FloorName;
            floor.transform.SetParent(root.transform, worldPositionStays: false);
            floor.transform.localPosition = Vector3.zero;
            floor.transform.localRotation = Quaternion.identity;
            // Unity's built-in Plane is 10×10 m at unit scale.
            floor.transform.localScale    = new Vector3(floorSize / 10f, 1f, floorSize / 10f);
        }

        void EnsureSource(GameObject root)
        {
            var src = root.transform.Find(SourceName)?.gameObject;
            if (src == null)
            {
                src = new GameObject(SourceName);
                src.transform.SetParent(root.transform, worldPositionStays: false);
                Undo.RegisterCreatedObjectUndo(src, "Create synthetic DMX source");
            }
            if (src.GetComponent<VRSLProfilingSyntheticDMXSource>() == null)
                src.AddComponent<VRSLProfilingSyntheticDMXSource>();
        }

        void EnsureManager(GameObject root)
        {
            var existing = root.transform.Find(ManagerName)?.gameObject;
            if (path == LightingPath.LegacyMeshShader)
            {
                if (existing != null)
                    Undo.DestroyObjectImmediate(existing);
                return;
            }

#if VRSL_URP
            if (existing != null) return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(URPManager);
            if (prefab == null)
            {
                Debug.LogError($"[VRSL Profiling] URP manager prefab not found: {URPManager}");
                return;
            }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            inst.name = ManagerName;
            Undo.RegisterCreatedObjectUndo(inst, "Spawn URP light manager");

            EnsureManagerShaders(inst.GetComponent<VRSL.VRSL_URPLightManager>());
#endif
        }

#if VRSL_URP
        // Defensive: assign the volumetric and lighting shaders on the spawned
        // manager if the prefab didn't carry refs. Profiling without the
        // volumetric shader silently produces only floor lighting (no cones),
        // which gives misleading "URP looks cheap" numbers in the sweep.
        static void EnsureManagerShaders(VRSL.VRSL_URPLightManager mgr)
        {
            if (mgr == null) return;

            bool changed = false;
            if (mgr.lightingShader == null)
            {
                mgr.lightingShader = Shader.Find("Hidden/VRSL-URP/DeferredLighting");
                changed |= mgr.lightingShader != null;
            }
            if (mgr.volumetricShader == null)
            {
                mgr.volumetricShader = Shader.Find("Hidden/VRSL-URP/VolumetricLighting");
                changed |= mgr.volumetricShader != null;
            }
            if (changed) EditorUtility.SetDirty(mgr);
        }
#endif

        void RebuildTruss(GameObject root)
        {
            var trussTrans = root.transform.Find(TrussName);
            GameObject truss;
            if (trussTrans != null)
            {
                truss = trussTrans.gameObject;
                while (truss.transform.childCount > 0)
                    Undo.DestroyObjectImmediate(truss.transform.GetChild(0).gameObject);
            }
            else
            {
                truss = new GameObject(TrussName);
                truss.transform.SetParent(root.transform, worldPositionStays: false);
                Undo.RegisterCreatedObjectUndo(truss, "Create truss");
            }
            truss.transform.localPosition = new Vector3(0f, trussHeight, 0f);
            truss.transform.localRotation = Quaternion.identity;
            truss.transform.localScale    = Vector3.one;

            string asset = PrefabPath(path, fixtureType);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            if (prefab == null)
            {
                Debug.LogError($"[VRSL Profiling] Fixture prefab not found: {asset}");
                return;
            }

            float spacing = fixtureCount > 1 ? trussWidth / (fixtureCount - 1) : 0f;
            float startX  = -trussWidth * 0.5f;
            // Pitch the fixture so its cone fires downward. The mover head's
            // resting orientation aims along +Z, so a +X rotation tilts the
            // cone down toward the floor.
            var rot = Quaternion.Euler(fixtureTilt, 0f, 0f);

            for (int i = 0; i < fixtureCount; i++)
            {
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, truss.transform);
                inst.name = $"{prefab.name} ({i:000})";
                inst.transform.localPosition = fixtureCount == 1
                    ? Vector3.zero
                    : new Vector3(startX + spacing * i, 0f, 0f);
                inst.transform.localRotation = rot;
                ApplyFixtureSector(inst, i);
            }
        }

        // Both VRStageLighting_DMX_Static (legacy authoring component) and
        // VRStageLighting_DMX_RealtimeLight (URP authoring component) expose
        // identically-named SerializedProperty paths for sector addressing,
        // so a single sweep across MonoBehaviours on the prefab handles both
        // fixture families without taking a hard reference to either type.
        static void ApplyFixtureSector(GameObject inst, int sector)
        {
            foreach (var mb in inst.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
            {
                if (mb == null) continue;
                using var so = new SerializedObject(mb);
                var legacyMode = so.FindProperty("useLegacySectorMode");
                var sectorProp = so.FindProperty("sector");
                if (legacyMode == null || sectorProp == null) continue;

                legacyMode.boolValue = true;
                sectorProp.intValue  = sector;
                so.ApplyModifiedProperties();
            }
        }

        void EnsureCamera(GameObject root)
        {
            var camGO = root.transform.Find(CameraName)?.gameObject;
            if (camGO == null)
            {
                camGO = new GameObject(CameraName);
                camGO.transform.SetParent(root.transform, worldPositionStays: false);
                camGO.AddComponent<Camera>();
                Undo.RegisterCreatedObjectUndo(camGO, "Create profiling camera");
            }
            camGO.tag = "MainCamera";

            switch (cameraVariant)
            {
                case CameraVariant.InsideCones:
                    // Eye-height head pose at one end of the truss looking back
                    // along it. Most cones overlap in screen space — worst case
                    // for the URP fullscreen lighting and volumetric raymarch.
                    camGO.transform.position = new Vector3(0f, 1.6f, -trussWidth * 0.45f);
                    camGO.transform.rotation = Quaternion.Euler(15f, 0f, 0f);
                    break;
                case CameraVariant.OutsideCones:
                    // Same eye-height, but at the far end of the floor looking
                    // outward — truss and cones sit entirely behind the camera.
                    // Per-pixel cone contribution and volumetric in-scattering
                    // drop to ~0; the URP surface pass still evaluates all N
                    // fixtures per pixel (deferred lighting is view-agnostic).
                    // The Inside/Outside delta isolates the cone-overlap cost.
                    camGO.transform.position = new Vector3(0f, 1.6f, trussWidth * 0.45f);
                    camGO.transform.rotation = Quaternion.Euler(15f, 0f, 0f);
                    break;
            }
        }
    }
}
