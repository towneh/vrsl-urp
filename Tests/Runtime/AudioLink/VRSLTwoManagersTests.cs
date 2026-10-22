#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows GW1 to GW3 of TESTING.md: the DMX and AudioLink managers lighting one camera,
    /// with the scene's gobo wheel and disco ball cubemap the same for both whichever
    /// manager's camera callback runs last.
    ///
    /// The order is set by taking a manager's handler off
    /// <c>RenderPipelineManager.beginCameraRendering</c> and putting it back, which puts it
    /// last, rather than left to the order the managers happened to be enabled in.
    /// </summary>
    class VRSLTwoManagersTests : VRSLDMXTest
    {
        const string Pkg           = "Packages/town.mr.vrsl-urp/";
        const string ManagerPrefab = Pkg + "Runtime/Prefabs/AudioLink/VRSL-AudioLink-LightManager.prefab";
        const string BallPrefab    = Pkg + "Runtime/Prefabs/AudioLink/VRSL-AudioLink-Discoball.prefab";
        const string GoboFolder    = Pkg + "Runtime/Textures/MoverLightTextures/GOBO/IndividualGobos";
        const int ImageSize    = 512;
        const int WarmUpFrames = 4;

        enum Last { Dmx, AudioLink }

        // ── Order of the camera callbacks ─────────────────────────────────────

        static void MoveCallbackLast(MonoBehaviour manager)
        {
            var type = manager.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var subscribed = type.GetField("_injectionSubscribed", flags);
            Assert.IsNotNull(subscribed, $"{type.Name} has no _injectionSubscribed field to check");
            Assert.IsTrue((bool)subscribed.GetValue(manager),
                $"{type.Name} is not subscribed to beginCameraRendering, so there is no callback to order");
            var method = type.GetMethod("OnBeginCameraRendering", flags);
            Assert.IsNotNull(method, $"{type.Name} has no OnBeginCameraRendering");
            var handler = (Action<ScriptableRenderContext, Camera>)Delegate.CreateDelegate(
                typeof(Action<ScriptableRenderContext, Camera>), manager, method);
            RenderPipelineManager.beginCameraRendering -= handler;
            RenderPipelineManager.beginCameraRendering += handler;
        }

        static void Order(VRSLDMXRig rig, VRSL_AudioLinkURPLightManager audioLink, Last last)
            => MoveCallbackLast(last == Last.Dmx ? rig.Manager : audioLink);

        // ── The stage ─────────────────────────────────────────────────────────

        /// <summary>The DMX rig and an AudioLink manager from its prefab, both lighting the
        /// rig's camera with no surface prepass (the rows are about masks, not albedo) and
        /// no beams. <paramref name="dmxFixtures"/> 1 keeps the rig's mover, dark; 0 leaves
        /// the DMX manager holding nothing.</summary>
        sealed class Stage : IDisposable
        {
            public VRSLDMXRig Rig;
            public GameObject Root;
            public VRSL_AudioLinkURPLightManager AudioLink;

            public Stage(int dmxFixtures, Action<VRSL_DMXURPLightManager> setUpDmx,
                         Action<GameObject, VRSL_AudioLinkURPLightManager> setUpAudioLink)
            {
                try
                {
                    Rig = VRSLDMXRig.Build(fixtures: dmxFixtures, targetSize: ImageSize);
                    Rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
                    Rig.Source.speed   = 0f;
                    Rig.FreezeForImageCapture();
                    foreach (var f in Rig.Fixtures) f.maxIntensity = 0f;
                    var dmx = Rig.Manager;
                    dmx.surfacePropertiesShader = null;
                    dmx.quality = VRSLQuality.Off;
                    setUpDmx?.Invoke(dmx);
                    // Again, so the prepass is rebuilt without its shader and the wheel
                    // from whatever the row set.
                    dmx.enabled = false;
                    dmx.enabled = true;

                    // Built switched off, so the manager's first enable sees the row's
                    // settings rather than the prefab's.
                    Root = new GameObject("VRSL two managers");
                    Root.SetActive(false);
                    var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPrefab);
                    Assert.IsNotNull(prefab, ManagerPrefab);
                    AudioLink = Object.Instantiate(prefab, Root.transform)
                                      .GetComponent<VRSL_AudioLinkURPLightManager>();
                    AudioLink.surfacePropertiesShader = null;
                    AudioLink.quality = VRSLQuality.Off;
                    setUpAudioLink?.Invoke(Root, AudioLink);
                    Root.SetActive(true);
                    Assert.AreSame(AudioLink, VRSL_AudioLinkURPLightManager.Instance,
                                   "the row's AudioLink manager should own the singleton");
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (Root != null) Object.DestroyImmediate(Root);
                Root = null;
                Rig?.Dispose();
                Rig = null;
            }
        }

        static VRSLAudioLinkRealtimeLight Spot(GameObject root, string name, Vector3 at, int goboIndex)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.position = at;
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            var light = go.AddComponent<VRSLAudioLinkRealtimeLight>();
            light.fixtureType     = AudioLinkFixtureType.MoverSpotlight;
            light.enableAudioLink = false;
            light.emissionColor   = Color.white;
            light.maxIntensity    = 12f;
            light.spotAngle       = 50f;
            light.range           = 20f;
            light.goboIndex       = goboIndex;
            light.goboSpinSpeed   = 0f;
            return light;
        }

        static IEnumerator WarmUpAndRead(Stage stage, Action<Texture2D> onRead)
        {
            for (int i = 0; i < WarmUpFrames; i++)
            {
                yield return null;
                stage.Rig.RenderFrame();
            }
            onRead(VRSLImageCompare.Read(stage.Rig.Target));
        }

        // ── Gobos ─────────────────────────────────────────────────────────────

        /// <summary>Upstream's eight, Default first, the order the managers load them in.</summary>
        static Texture2D[] DefaultEight()
        {
            var list = new List<Texture2D>();
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Texture2D", new[] { GoboFolder }))
                list.Add(UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guid)));
            list.Sort((a, b) =>
            {
                bool aD = a.name.Contains("Default"), bD = b.name.Contains("Default");
                if (aD != bD) return aD ? -1 : 1;
                return string.Compare(a.name, b.name, StringComparison.Ordinal);
            });
            Assert.AreEqual(8, list.Count, $"expected upstream's eight gobos in {GoboFolder}");
            return list.ToArray();
        }

        /// <summary>A gobo open on one half only, which no shipped gobo looks like.</summary>
        static Texture2D HalfGobo(string name, bool leftOpen)
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { name = name };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    pixels[y * size + x] = (x < size / 2) == leftOpen
                        ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Where <paramref name="world"/> lands in the rig's frame, in pixels.</summary>
        static Vector2Int PixelOf(VRSLDMXRig rig, Vector3 world)
        {
            var vp = rig.Camera.WorldToViewportPoint(world);
            return new Vector2Int(Mathf.RoundToInt(vp.x * rig.Target.width),
                                  Mathf.RoundToInt(vp.y * rig.Target.height));
        }

        /// <summary>Pixels in a box round <paramref name="centre"/> that differ by more than
        /// a few steps between two frames, as a percentage of the box.</summary>
        static float DifferingAround(Vector2Int centre, Texture2D a, Texture2D b)
        {
            const int half = 56;
            int cx = centre.x, cy = centre.y;
            var pa = a.GetPixels32();
            var pb = b.GetPixels32();
            int differing = 0, total = 0;
            for (int y = Mathf.Max(0, cy - half); y < Mathf.Min(a.height, cy + half); y++)
                for (int x = Mathf.Max(0, cx - half); x < Mathf.Min(a.width, cx + half); x++)
                {
                    int i = y * a.width + x;
                    total++;
                    if (Mathf.Abs(pa[i].r - pb[i].r) > 8 || Mathf.Abs(pa[i].g - pb[i].g) > 8
                        || Mathf.Abs(pa[i].b - pb[i].b) > 8) differing++;
                }
            return total == 0 ? 0f : 100f * differing / total;
        }

        static readonly Vector3 LightA = new(7f, 4.5f, 4f);
        static readonly Vector3 LightB = new(13f, 4.5f, 4f);

        /// <summary>
        /// Two AudioLink spots on gobos 3 and 9, the DMX manager holding one wheel and the
        /// AudioLink manager another. The DMX wheel is upstream's eight with a half-open
        /// gobo in slot 3 and another in a ninth slot; the AudioLink wheel is upstream's
        /// eight. With one wheel for the scene both lights show the DMX wheel's gobos.
        /// </summary>
        static IEnumerator CaptureGobos(int dmxFixtures, Texture2D[] dmxWheel, Texture2D[] audioLinkWheel,
                                        Last last, Action<Texture2D> onRead)
        {
            using var stage = new Stage(dmxFixtures,
                dmx =>
                {
                    dmx.goboWheel    = null;
                    dmx.goboTextures = dmxWheel;
                },
                (root, al) =>
                {
                    al.goboWheel    = null;
                    al.goboTextures = audioLinkWheel;
                    Spot(root, "AudioLink spot, gobo 3", LightA, goboIndex: 3);
                    Spot(root, "AudioLink spot, gobo 9", LightB, goboIndex: 9);
                });
            Order(stage.Rig, stage.AudioLink, last);
            s_poolA = PixelOf(stage.Rig, new Vector3(LightA.x, 0f, LightA.z));
            s_poolB = PixelOf(stage.Rig, new Vector3(LightB.x, 0f, LightB.z));
            yield return WarmUpAndRead(stage, onRead);
        }

        // Where the two pools land in the frame. The rig's camera never moves.
        static Vector2Int s_poolA, s_poolB;

        static IEnumerator GobosRow(string row, int dmxFixtures)
        {
            var eight = DefaultEight();
            var halfLeft  = HalfGobo("half open left", leftOpen: true);
            var halfRight = HalfGobo("half open right", leftOpen: false);
            var dmxWheel = new Texture2D[9];
            Array.Copy(eight, dmxWheel, 8);
            dmxWheel[2] = halfLeft;
            dmxWheel[8] = halfRight;

            var frames = new List<Texture2D>();
            Texture2D shared = null, other = null, dmxLast = null, audioLinkLast = null;
            try
            {
                // References: both managers holding the DMX wheel, and both holding the
                // AudioLink one. The row is only meaningful when they differ under each light.
                yield return CaptureGobos(dmxFixtures, dmxWheel, dmxWheel, Last.AudioLink, t => frames.Add(shared = t));
                yield return CaptureGobos(dmxFixtures, eight, eight, Last.AudioLink, t => frames.Add(other = t));
                yield return CaptureGobos(dmxFixtures, dmxWheel, eight, Last.Dmx, t => frames.Add(dmxLast = t));
                yield return CaptureGobos(dmxFixtures, dmxWheel, eight, Last.AudioLink, t => frames.Add(audioLinkLast = t));

                float apart3 = DifferingAround(s_poolA, shared, other);
                float apart9 = DifferingAround(s_poolB, shared, other);
                float dmx3   = DifferingAround(s_poolA, dmxLast, shared);
                float dmx9   = DifferingAround(s_poolB, dmxLast, shared);
                float al3    = DifferingAround(s_poolA, audioLinkLast, shared);
                float al9    = DifferingAround(s_poolB, audioLinkLast, shared);
                Debug.Log($"[{row}] the two wheels differ on {apart3:F2}% round gobo 3 and {apart9:F2}% round gobo 9; "
                        + $"DMX callback last differs from the shared wheel on {dmx3:F2}% / {dmx9:F2}%, "
                        + $"AudioLink last on {al3:F2}% / {al9:F2}%");

                // Frames of one wheel match to 0.00%, so a few percent apart is a wide margin.
                Assert.Greater(apart3, 2f, "the row can't tell the two wheels apart under gobo 3");
                Assert.Greater(apart9, 2f, "the row can't tell the two wheels apart under gobo 9");
                Assert.Less(dmx3, 0.5f, "with the DMX manager's callback last, the gobo 3 light isn't showing the scene's wheel");
                Assert.Less(dmx9, 0.5f, "with the DMX manager's callback last, the gobo 9 light isn't showing the scene's wheel");
                Assert.Less(al3, 0.5f, "with the AudioLink manager's callback last, the gobo 3 light isn't showing the scene's wheel");
                Assert.Less(al9, 0.5f, "with the AudioLink manager's callback last, the gobo 9 light isn't showing the scene's wheel");
            }
            finally
            {
                foreach (var f in frames) Object.DestroyImmediate(f);
                Object.DestroyImmediate(halfLeft);
                Object.DestroyImmediate(halfRight);
            }
        }

        /// <summary>GW2. A gobo on the DMX manager's wheel only reaches the AudioLink
        /// lights, in slot 3 and in a ninth slot, whichever callback runs last.</summary>
        [UnityTest]
        public IEnumerator GW2_one_wheel_for_both_managers_whichever_callback_runs_last()
            => GobosRow("GW2", dmxFixtures: 1);

        /// <summary>GW3. GW2 with the DMX manager enabled and holding no fixtures.</summary>
        [UnityTest]
        public IEnumerator GW3_one_wheel_with_the_DMX_manager_holding_no_fixtures()
            => GobosRow("GW3", dmxFixtures: 0);

        // ── The disco ball ────────────────────────────────────────────────────

        static IEnumerator CaptureBall(Cubemap dmxCube, Cubemap audioLinkCube, bool ballOn, Last last,
                                       Action<Texture2D> onRead)
        {
            using var stage = new Stage(1,
                dmx => dmx.discoballCubemap = dmxCube,
                (root, al) =>
                {
                    al.discoballCubemap = audioLinkCube;
                    var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BallPrefab);
                    Assert.IsNotNull(prefab, BallPrefab);
                    var ball = Object.Instantiate(prefab, root.transform);
                    ball.transform.position = BallAbove;
                    var light = ball.GetComponent<VRSLAudioLinkRealtimeLight>();
                    Assert.IsNotNull(light, "the AudioLink discoball prefab carries no realtime light");
                    light.enableAudioLink    = false;
                    light.discoballSpinSpeed = 0f;
                    light.maxIntensity       = ballOn ? 30f : 0f;
                });
            Order(stage.Rig, stage.AudioLink, last);
            yield return WarmUpAndRead(stage, onRead);
        }

        // Above the floor where the rig's camera looks.
        static readonly Vector3 BallAbove = new(10f, 3f, 4f);

        static float LitByBall(Texture2D frame, Texture2D dark)
        {
            var a = frame.GetPixels32();
            var b = dark.GetPixels32();
            int lit = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i].r - b[i].r > 8 || a[i].g - b[i].g > 8 || a[i].b - b[i].b > 8) lit++;
            return 100f * lit / a.Length;
        }

        /// <summary>GW1. An AudioLink disco ball beside a DMX manager with no cubemap: its
        /// dots land on the floor whichever manager's callback runs last.</summary>
        [UnityTest]
        public IEnumerator GW1_the_disco_ball_keeps_its_dots_beside_a_manager_with_no_cubemap()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPrefab);
            var cube = prefab.GetComponent<VRSL_AudioLinkURPLightManager>().discoballCubemap;
            Assert.IsNotNull(cube, "the AudioLink manager prefab ships no disco ball cubemap");

            var frames = new List<Texture2D>();
            Texture2D dark = null, flood = null, dmxLast = null, audioLinkLast = null;
            try
            {
                yield return CaptureBall(null, cube, ballOn: false, Last.AudioLink, t => frames.Add(dark = t));
                yield return CaptureBall(null, null, ballOn: true, Last.AudioLink, t => frames.Add(flood = t));
                yield return CaptureBall(null, cube, ballOn: true, Last.Dmx, t => frames.Add(dmxLast = t));
                yield return CaptureBall(null, cube, ballOn: true, Last.AudioLink, t => frames.Add(audioLinkLast = t));

                float flooded = LitByBall(flood, dark);
                float dmxDots = LitByBall(dmxLast, dark);
                float alDots  = LitByBall(audioLinkLast, dark);
                Debug.Log($"[GW1] the ball lights {flooded:F2}% of the frame with no cubemap anywhere, "
                        + $"{dmxDots:F2}% with the DMX callback last, {alDots:F2}% with the AudioLink callback last");

                Assert.Greater(flooded, 5f, $"the ball with no cubemap lit only {flooded:F2}% of the frame; the row can't tell dots from nothing");
                Assert.Greater(alDots, 0.2f, $"with the AudioLink callback last the ball lit {alDots:F2}%: no dots landed at all");
                Assert.Less(alDots, flooded * 0.6f, $"with the AudioLink callback last the ball lit {alDots:F2}% against {flooded:F2}% with no cubemap: no dots");
                Assert.Greater(dmxDots, 0.2f, $"with the DMX callback last the ball lit {dmxDots:F2}%: no dots landed at all");
                Assert.Less(dmxDots, flooded * 0.6f,
                    $"with the DMX callback last the ball lit {dmxDots:F2}% against {flooded:F2}% with no cubemap: "
                  + "the manager with no cubemap took the dots away");
            }
            finally
            {
                foreach (var f in frames) Object.DestroyImmediate(f);
            }
        }
    }
}
#endif
