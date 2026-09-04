#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The discoball as a realtime light: a point light whose dots are a cubemap
    /// looked up along the direction from the ball, turned by the ball's spin.
    /// K1 reads the light record the compute writes for it; K2 looks at the floor.
    /// </summary>
    class VRSLDiscoballTests : VRSLDMXTest
    {
        const string Prefab  = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/VRSL-DMX-URP-Discoball-1CH.prefab";
        const string Cubemap = "Packages/town.mr.vrsl-urp/Runtime/Textures/VRSL-DiscoBallCubeMap.png";
        const int ImageSize  = 512;
        const int WarmUpFrames = 4;

        /// <summary>Hang a discoball from the prefab above the rig's floor, patched at
        /// the channel whose dimmer the Fixtures pattern holds at full.</summary>
        static VRStageLighting_DMX_RealtimeLight Hang(VRSLDMXRig rig, float spinDegPerSec)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
            Assert.IsNotNull(prefab, $"the discoball prefab is not at {Prefab}");
            var go = Object.Instantiate(prefab, rig.Floor.transform.parent);
            go.name = "Discoball";
            go.transform.position = rig.Floor.transform.position + new Vector3(0f, 3f, 0f);
            var ball = go.GetComponent<VRStageLighting_DMX_RealtimeLight>();
            Assert.IsNotNull(ball, "the discoball prefab carries no realtime light");
            Assert.AreEqual(DMXFixtureType.Discoball, ball.fixtureType, "the prefab is not a discoball");
            // The Fixtures pattern holds every thirteenth channel's dimmer at full, so a
            // one-channel fixture patched on one of them reads full.
            ball.useLegacySectorMode = false;
            ball.dmxUniverse = 1;
            ball.dmxChannel  = 6;
            ball.discoballSpinSpeed = spinDegPerSec;
            ball.maxIntensity = 30f;
            rig.Manager.RefreshFixtures();
            return ball;
        }

        static bool IsDiscoball(VRSL_URPLightManager.VRSLLightData l)
            => Mathf.Repeat(l.directionAndType.w, 4f) > 1.5f;

        /// <summary>K1. The compute writes the discoball as its own light type, without
        /// a gobo, with the ball's up axis as its spin axis, lit, and turning at the
        /// authored rate.</summary>
        [UnityTest]
        public IEnumerator K1_the_light_record_is_a_spinning_discoball()
        {
            using var rig = VRSLDMXRig.Build(fixtures: 1);
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
            rig.Source.speed   = 0f;
            var ball = Hang(rig, spinDegPerSec: 90f);
            rig.Manager.enabled = false;
            rig.Manager.enabled = true;
            rig.Manager.RefreshFixtures();

            yield return rig.Step(3);
            var first = FindBall(rig);

            Assert.Greater(first.colorAndIntensity.w, 0f, "the discoball is not lit; its dimmer channel reads zero");
            Assert.AreEqual(-1f, Mathf.Floor(first.directionAndType.w * 0.25f) - 1f, 0.01f,
                "a discoball carries a gobo slice; it should carry none");
            var axis = new Vector3(first.directionAndType.x, first.directionAndType.y, first.directionAndType.z);
            Assert.Greater(Vector3.Dot(axis, ball.transform.up), 0.999f,
                $"the spin axis {axis} is not the ball's up {ball.transform.up}");
            Assert.AreEqual(1f, first.colorAndIntensity.x, 0.01f, "a white tint should read white");

            int frames = 12;
            yield return rig.Step(frames);
            var later = FindBall(rig);

            float expected = 90f * Mathf.Deg2Rad * VRSLDMXRig.FrameDelta * frames;
            float turned   = Mathf.DeltaAngle(first.spotParams.w * Mathf.Rad2Deg,
                                              later.spotParams.w * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            Debug.Log($"[K1] spin phase {first.spotParams.w:F3} → {later.spotParams.w:F3} over {frames} frames, expected {expected:F3}");
            Assert.AreEqual(expected, turned, expected * 0.25f,
                "the ball did not turn at the authored rate between two reads");
        }

        static VRSL_URPLightManager.VRSLLightData FindBall(VRSLDMXRig rig)
        {
            var lights = rig.ReadRaw();
            int found = -1;
            for (int i = 0; i < lights.Length; i++)
                if (IsDiscoball(lights[i])) { Assert.AreEqual(-1, found, "two records read as discoballs"); found = i; }
            Assert.GreaterOrEqual(found, 0, "no light record reads as a discoball");
            return lights[found];
        }

        /// <summary>K2. A cubemap that is black but for one face carves the flood a
        /// plain point light makes into that face's share; the shipped cubemap does
        /// the same with its dots. The lit area is the observable.</summary>
        [UnityTest]
        public IEnumerator K2_the_cubemap_carves_the_floor_into_dots()
        {
            var shipped = UnityEditor.AssetDatabase.LoadAssetAtPath<Cubemap>(Cubemap);
            Assert.IsNotNull(shipped, $"the shipped cubemap is not at {Cubemap}");

            // One face lit, five dark: from a ball above the floor, only the floor
            // straight below it inside that face's 90° pyramid receives light.
            var oneFace = new Cubemap(8, TextureFormat.RGBA32, false) { name = "one face" };
            for (int face = 0; face < 6; face++)
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        oneFace.SetPixel((CubemapFace)face, x, y,
                            (CubemapFace)face == CubemapFace.NegativeY ? Color.white : Color.black);
            oneFace.Apply(false, true);

            Texture2D flooded = null, faced = null, dotted = null, dark = null;
            try
            {
                yield return Capture(null, t => dark = t, ballOn: false);
                yield return Capture(null, t => flooded = t);
                yield return Capture(oneFace, t => faced = t);
                yield return Capture(shipped, t => dotted = t);
            }
            finally { Object.Destroy(oneFace); }

            // Judged against the frame with the ball off: the floor carries a base
            // brightness of its own, so the observable is what the ball adds to it.
            float litFlood = LitByBall(flooded, dark);
            float litFace  = LitByBall(faced, dark);
            float litDots  = LitByBall(dotted, dark);
            Debug.Log($"[K2] the ball lights {litFlood:F2}% of the frame with no cubemap (mean {Mean(flooded):F1} over {Mean(dark):F1}), "
                    + $"{litFace:F2}% with one face (mean {Mean(faced):F1}), {litDots:F2}% with the shipped dots (mean {Mean(dotted):F1})");

            Assert.Greater(litFlood, 5f,
                $"the ball with no cubemap lit only {litFlood:F2}% of the frame; the row cannot tell a mask from nothing");
            Assert.Greater(litFace, 0.2f,
                $"the one-face cubemap lit {litFace:F2}% of the frame: the face under the ball landed nothing");
            Assert.Less(litFace, litFlood * 0.6f,
                $"the one-face cubemap left {litFace:F2}% lit against {litFlood:F2}% with none: the mask is not being applied");
            Assert.Greater(litDots, 0.2f,
                $"the shipped cubemap lit {litDots:F2}% of the frame: no dots landed at all");
            Assert.Less(litDots, litFlood * 0.6f,
                $"the shipped cubemap left {litDots:F2}% lit against {litFlood:F2}% with none; its import is not giving the shader dots");
        }

        static IEnumerator Capture(Cubemap cube, System.Action<Texture2D> onCaptured, bool ballOn = true)
        {
            using var rig = VRSLDMXRig.Build(fixtures: 1, targetSize: ImageSize);
            rig.Source.pattern = VRSL_SyntheticDMXChannelSource.Pattern.Fixtures;
            rig.Source.speed   = 0f;
            // Lit as neutral grey rather than through the surface prepass: what the
            // prepass captures for the floor depends on the host's priming state (S14),
            // and this row is about the cubemap, not the floor's albedo.
            rig.Manager.surfacePropertiesShader = null;
            rig.Manager.enabled = false;
            rig.Manager.enabled = true;
            rig.FreezeForImageCapture();
            // The one mover is there for the rig's calibration; the floor is the ball's.
            foreach (var f in rig.Fixtures) f.maxIntensity = 0f;
            var ball = Hang(rig, spinDegPerSec: 0f);
            if (!ballOn) ball.maxIntensity = 0f;
            rig.Manager.discoballCubemap = cube;
            rig.Manager.MarkConfigDirty();

            for (int i = 0; i < WarmUpFrames; i++)
            {
                yield return null;
                rig.RenderFrame();
            }
            var record = FindBall(rig);
            Debug.Log($"[K2] capture with {(cube ? cube.name : "no cubemap")}, ball {(ballOn ? "on" : "off")}: "
                    + $"intensity {record.colorAndIntensity.w:F2} at {record.positionAndRange.x:F1},{record.positionAndRange.y:F1},{record.positionAndRange.z:F1} "
                    + $"range {record.positionAndRange.w:F1}, camera at {rig.Camera.transform.position}, floor at {rig.Floor.transform.position}");
            onCaptured(VRSLImageCompare.Read(rig.Target));
        }

        static float Mean(Texture2D frame)
        {
            var pixels = frame.GetPixels32();
            double sum = 0;
            foreach (var p in pixels) sum += (p.r + p.g + p.b) / 3.0;
            return (float)(sum / pixels.Length);
        }

        /// <summary>Percentage of the frame the ball brightened by more than a few
        /// steps over the same frame with the ball off.</summary>
        static float LitByBall(Texture2D frame, Texture2D dark)
        {
            var a = frame.GetPixels32();
            var b = dark.GetPixels32();
            Assert.AreEqual(b.Length, a.Length, "the two captures differ in size");
            int lit = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i].r - b[i].r > 8 || a[i].g - b[i].g > 8 || a[i].b - b[i].b > 8) lit++;
            return 100f * lit / a.Length;
        }
    }
}
#endif
