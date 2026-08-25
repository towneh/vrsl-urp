// Editor-only in full: it loads package prefabs through the AssetDatabase, which a
// player has no equivalent of. It lives in the runtime assembly regardless, because
// the suite reaches it and the test assembly does not reference the editor one — the
// same arrangement the PlayMode rig uses for the same reason.
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// The scene the standard sweep is measured in.
    ///
    /// Built here rather than through the profiling sample's window, for one reason
    /// that decides the whole shape of a sweep: that builder only runs in edit mode,
    /// so driving the matrix through it costs a play-mode round trip per
    /// configuration — thirty of them, each with a domain reload. Building the truss
    /// at its largest once and activating a subset of it at runtime puts the entire
    /// matrix inside a single play-mode session, which is the difference between a
    /// sweep that takes a couple of minutes and one that takes most of a coffee
    /// break. It also drops the sample from the sweep's requirements.
    ///
    /// <b>The subset is evenly spaced, not the first N.</b> Taking the first ten of
    /// two hundred clusters them at one end of the truss, which changes lights per
    /// tile and empty tile count — the exact counters the sweep exists to report.
    /// Every k-th fixture keeps the spread identical at every count, so the only
    /// thing varying down the fixture-count axis is the number of them.
    /// </summary>
    static class VRSLBenchmarkScene
    {
        const string Pkg           = "Packages/town.mr.vrsl-urp/";
        const string ManagerPrefab = Pkg + "Runtime/Prefabs/DMX/Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-URP-LightManager-Horizontal.prefab";
        const string FixturePrefab = Pkg + "Runtime/Prefabs/DMX/Horizontal Mode/DMX-13CH-URP-Fixtures/VRSL-DMX-Mover-Spotlight-H-13CH-URP.prefab";

        public const string RootName   = "VRSL Benchmark Scene";
        public const string CameraName = "Benchmark Camera";
        /// <summary>Looked up by name to activate a subset, so it is not a literal.</summary>
        public const string TrussName  = "Truss";

        /// <summary>The largest count in the matrix. The truss is built at this size
        /// and everything smaller is a subset of it.</summary>
        public const int MaxFixtures = 200;

        /// <summary>Fixture counts the standard sweep runs.</summary>
        public static readonly int[] FixtureCounts = { 10, 25, 50, 100, 200 };

        public enum CameraVariant
        {
            /// <summary>Camera among the beams. The worst case: most of the screen is
            /// inside a cone, so most tiles carry most of the fixtures.</summary>
            InsideCones,
            /// <summary>Camera outside looking in, which is what culling should be
            /// good at. The gap between the two is the cull doing its job.</summary>
            OutsideCones,
        }

        public static readonly CameraVariant[] CameraVariants =
            { CameraVariant.InsideCones, CameraVariant.OutsideCones };

        /// <summary>
        /// The size the sweep renders at, regardless of the Game view.
        ///
        /// Two reasons, and the second is the one that forced it. A matrix measured at
        /// whatever size a window happens to be is not the fixed capture setup R-M0-1
        /// asks for, and two machines could not be compared even in principle. And at
        /// the Game view's size there was not enough per-pixel work to measure at all:
        /// measured 2026-08-24, lights per tile rising from 9 to 47 moved the GPU frame
        /// from 0.88 ms to 0.85 ms, which is to say not at all, while the package's cost
        /// wandered between -0.37 and +0.34 ms with a stated precision of 0.06.
        /// </summary>
        public const int CaptureWidth  = 1920;
        public const int CaptureHeight = 1080;

        /// <summary>
        /// Usable 13-channel sectors per universe.
        ///
        /// A VRSL universe is 520 flat slots, not 512: the grid is 13 channels wide and
        /// 512 does not divide by 13, so each universe is padded out to 40 whole rows.
        /// The last of those rows, sector 39, spans flat 508 to 520 — inside the padding
        /// no desk can address, since a 13-channel fixture cannot straddle the 512
        /// boundary. So 39 sectors are patchable per universe and the fortieth is a dead
        /// zone.
        /// </summary>
        const int UsableSectorsPerUniverse = 39;

        /// <summary>
        /// The sector fixture <paramref name="index"/> is patched at, skipping every
        /// universe's dead zone.
        ///
        /// Assigning the index directly puts one fixture in forty somewhere unpatchable,
        /// where it reads padding and never lights. At 200 fixtures that was five of
        /// them, and the sweep reported 190 emitting while claiming 200 — a workload ten
        /// fixtures lighter than the row it was filed under.
        /// </summary>
        static int SectorFor(int index) =>
            index / UsableSectorsPerUniverse * 40 + index % UsableSectorsPerUniverse;

        /// <summary>Universes the source must publish to reach the highest sector in
        /// use, derived rather than written down so the two cannot drift apart.</summary>
        static int RequiredUniverses =>
            Mathf.CeilToInt((SectorFor(MaxFixtures - 1) * 13 + 13) / (float)VRSLDMX.SlotsPerUniverse);

        const float TrussWidth  = 30f;
        const float TrussHeight = 6f;
        const float FloorSize   = 150f;

        /// <summary>
        /// Open a fresh empty scene and build the sweep into it. Edit mode only — it
        /// loads package prefabs through the AssetDatabase.
        ///
        /// <b>A new scene, not the one that happens to be open.</b> Building alongside
        /// existing content puts a second light manager in the scene, and the manager
        /// is a singleton — the loser destroys itself in <c>Awake</c> and the sweep
        /// then measures whichever survived, across both scenes' fixtures, through
        /// whichever camera <c>Camera.main</c> resolved to. Every row would carry a
        /// number and none of them would be of the configuration it names.
        ///
        /// It also closes a determinism hole: a matrix measured in an unknown scene is
        /// not a fixed capture setup, whatever else is pinned down.
        ///
        /// Returns null if the user declines to save their open scene.
        /// </summary>
        public static GameObject Build()
        {
            // Shows Save / Don't Save / Cancel, and returns false only on Cancel. No
            // dialog at all when nothing is dirty. Cancel aborts before the new scene
            // replaces anything, so nothing is lost either way.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return null;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            return Populate();
        }

        /// <summary>
        /// Where the record of switched-off directional lights is kept.
        ///
        /// In SessionState rather than a static field, because a static does not survive
        /// the domain reload that entering play mode causes — and this scene is built in
        /// edit mode and then measured in play mode, so that reload sits squarely in the
        /// middle of the one path that matters. The record would be gone while the lights
        /// stayed off, and the author would be left with a darkened scene and nothing to
        /// put back from.
        ///
        /// Scene paths rather than references, since an object reference does not survive
        /// a reload either.
        /// </summary>
        const string DisabledLightsKey = "VRSL.Perf.DisabledLights";

        static List<string> DisabledLights
        {
            get
            {
                string stored = SessionState.GetString(DisabledLightsKey, "");
                var paths = new List<string>();
                if (string.IsNullOrEmpty(stored)) return paths;
                foreach (string path in stored.Split('\n'))
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                return paths;
            }
            set => SessionState.SetString(DisabledLightsKey, string.Join("\n", value));
        }

        /// <summary>Hierarchy path of an object, which is what survives a reload.</summary>
        static string PathOf(GameObject go)
        {
            string path = go.name;
            for (var t = go.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return path;
        }

        /// <summary>
        /// Put back what <see cref="Populate"/> changed outside its own root. Call it
        /// when disposing of the root; destroying the root alone does not do this,
        /// because the lights were never inside it.
        /// </summary>
        public static void RestoreScene()
        {
            var paths = DisabledLights;
            if (paths.Count == 0) return;

            // Found by path rather than held as references, since neither a reference nor
            // a static list survives the domain reload between building this scene and
            // measuring it.
            foreach (var light in Object.FindObjectsByType<Light>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (light == null || light.type != LightType.Directional) continue;
                if (paths.Contains(PathOf(light.gameObject))) light.gameObject.SetActive(true);
            }
            SessionState.EraseString(DisabledLightsKey);
        }

        /// <summary>
        /// Build the sweep's objects into the scene that is already open, and hand back
        /// the root so the caller can dispose of it.
        ///
        /// Separate from <see cref="Build"/> because that one opens a scene, and a test
        /// must do neither of the things that involves: a modal save prompt would hang a
        /// headless run, and swapping the scene would pull the ground out from under the
        /// test runner mid-fixture. The suite builds into the scene it was given and
        /// destroys the root afterwards.
        /// </summary>
        public static GameObject Populate()
        {
            foreach (var stale in Object.FindObjectsByType<GameObject>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (stale != null && stale.name == RootName)
                    Object.DestroyImmediate(stale);

            var root = new GameObject(RootName);

            // A directional light would add its own cost to every row and vary with
            // whatever the open scene happened to contain.
            //
            // Remembered so it can be put back. These live outside the root, so destroying
            // the root does not restore them — a row that called Populate would otherwise
            // leave every later row in a scene it had quietly darkened, and a person could
            // save the scene that way without noticing.
            // Put back anything a previous Populate switched off before forgetting it.
            // Clearing first loses the record, and a second Populate without an
            // intervening RestoreScene would leave those lights off for good.
            RestoreScene();
            var disabled = new List<string>();
            foreach (var light in Object.FindObjectsByType<Light>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (light == null || light.type != LightType.Directional) continue;
                if (!light.gameObject.activeSelf) continue;
                disabled.Add(PathOf(light.gameObject));
                light.gameObject.SetActive(false);
            }
            DisabledLights = disabled;

            try
            {
                BuildFloor(root);
                BuildTruss(root);
                BuildManager(root);
                BuildCamera(root);
            }
            catch
            {
                // The lights go off before any prefab is loaded, and both prefab loads
                // throw when their path has moved. Without this a renamed asset leaves the
                // author's scene darkened around a half-built root, with no root handed
                // back for anyone to dispose of — which is the case the explicit throw
                // exists to report in the first place.
                Object.DestroyImmediate(root);
                RestoreScene();
                throw;
            }
            return root;
        }

        static void BuildFloor(GameObject root)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.SetParent(root.transform, false);
            floor.transform.localScale = new Vector3(FloorSize / 10f, 1f, FloorSize / 10f);
        }

        static void BuildTruss(GameObject root)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(FixturePrefab);
            if (source == null)
                throw new System.InvalidOperationException($"Fixture prefab not found: {FixturePrefab}");

            var truss = new GameObject(TrussName);
            truss.transform.SetParent(root.transform, false);

            for (int i = 0; i < MaxFixtures; i++)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(source, truss.transform);
                instance.name = $"Fixture ({i:000})";

                float t = MaxFixtures == 1 ? 0.5f : i / (float)(MaxFixtures - 1);
                instance.transform.localPosition =
                    new Vector3(Mathf.Lerp(-TrussWidth * 0.5f, TrussWidth * 0.5f, t), TrussHeight, 0f);
                instance.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

                // Legacy sector mode walks the flat channel space directly, so a sector
                // is all the addressing a fixture needs — but not every sector is
                // patchable. See SectorFor.
                foreach (var fixture in instance.GetComponentsInChildren<VRStageLighting_DMX_RealtimeLight>())
                {
                    fixture.useLegacySectorMode = true;
                    fixture.sector = SectorFor(i);
                }
            }
        }

        static void BuildManager(GameObject root)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPrefab);
            if (source == null)
                throw new System.InvalidOperationException($"Manager prefab not found: {ManagerPrefab}");

            var manager = (GameObject)PrefabUtility.InstantiatePrefab(source, root.transform);
            manager.name = "VRSL URP Light Manager";

            // A deterministic channel source, so the fixtures are lit and moving the
            // same way on the same frame index of every run. Ramp gives every channel
            // a distinct, frame-stable value.
            var sourceGo = new GameObject("Synthetic DMX Source");
            sourceGo.transform.SetParent(root.transform, false);
            var synthetic = sourceGo.AddComponent<VRSL_SyntheticDMXChannelSource>();
            synthetic.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
            synthetic.universes = RequiredUniverses;
        }

        static void BuildCamera(GameObject root)
        {
            var camera = new GameObject(CameraName).AddComponent<Camera>();
            camera.transform.SetParent(root.transform, false);
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.tag             = "MainCamera";
            PoseCamera(camera, CameraVariant.InsideCones);
        }

        /// <summary>Put the camera where a variant says. Called per configuration at
        /// runtime, which is why it takes a camera rather than finding one.</summary>
        public static void PoseCamera(Camera camera, CameraVariant variant)
        {
            if (camera == null) return;
            if (variant == CameraVariant.InsideCones)
            {
                camera.transform.SetPositionAndRotation(
                    new Vector3(0f, 2f, 0f), Quaternion.Euler(-20f, 0f, 0f));
            }
            else
            {
                camera.transform.SetPositionAndRotation(
                    new Vector3(0f, 3f, -28f), Quaternion.Euler(5f, 0f, 0f));
            }
        }

        /// <summary>
        /// The sweep's own camera, found by name rather than through
        /// <c>Camera.main</c>.
        ///
        /// <c>Camera.main</c> returns whichever camera is tagged MainCamera, and in a
        /// host project that can be one the client spawned rather than this one — in
        /// which case the sweep poses somebody else's camera per configuration and
        /// measures a view it never set.
        /// </summary>
        public static Camera FindCamera(GameObject root)
        {
            var transform = root != null ? root.transform.Find(CameraName) : null;
            return transform != null ? transform.GetComponent<Camera>() : null;
        }

        /// <summary>Cameras that will actually render this frame. More than the
        /// sweep's own means every pass runs more than once and the cost is of both
        /// views together.</summary>
        public static int RenderingCameraCount()
        {
            int count = 0;
            foreach (var camera in Object.FindObjectsByType<Camera>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (camera != null && camera.enabled) count++;
            return count;
        }

        /// <summary>
        /// Activate an evenly-spaced <paramref name="count"/> of the truss and
        /// deactivate the rest, then tell the manager to re-collect.
        ///
        /// <c>RefreshFixtures</c> is what makes the change take: the manager holds
        /// its fixture list and its GPU buffers from when it last collected, so a
        /// deactivated fixture keeps its slot and keeps being lit until it does.
        /// </summary>
        /// <returns>The count actually activated, which is not always the count asked
        /// for. A row that silently measured fewer fixtures than its label claims is a row
        /// reporting a workload that never ran — see the sector dead zone, which cost
        /// exactly that before it was found.</returns>
        public static int SetActiveFixtures(GameObject root, int count)
        {
            var truss = root != null ? root.transform.Find(TrussName) : null;
            if (truss == null) return 0;

            int total = truss.childCount;
            count = Mathf.Clamp(count, 1, total);

            var keep = new HashSet<int>();
            for (int i = 0; i < count; i++)
                keep.Add(Mathf.Min(total - 1, Mathf.RoundToInt(i * (total - 1) / (float)Mathf.Max(1, count - 1))));

            // Rounding can collide on two indices at small counts, which would leave
            // fewer fixtures active than the row claims. Fill from the front with
            // whatever is still spare until the count is honest.
            for (int i = 0; i < total && keep.Count < count; i++) keep.Add(i);

            for (int i = 0; i < total; i++)
                truss.GetChild(i).gameObject.SetActive(keep.Contains(i));

            if (VRSL_URPLightManager.Instance != null)
                VRSL_URPLightManager.Instance.RefreshFixtures();

            return keep.Count;
        }
    }
}
#endif
