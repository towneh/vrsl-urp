#if UNITY_EDITOR
using System.Collections;
using Unity.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The club's leak in the rig: a room, an inner wall, a stairway behind it and an outer
    /// wall beyond. A light in the room aimed at the inner wall lights the stairway through
    /// it; zoned to a box whose face sits inside the inner wall, it lights the room as before
    /// and leaves the stairway dark.
    /// </summary>
    sealed class VRSLZoneRoom
    {
        public const float WallThickness = 0.3f;
        public const float OuterWallX    = 3f;
        public static readonly Vector3 LightAt = new(-4f, 3f, 0f);

        /// <summary>From the room, looking at the inner wall's near face.</summary>
        public static readonly (Vector3 eye, Vector3 at) Room = (new Vector3(-8.5f, 3f, -6f), new Vector3(0f, 2.5f, 0f));
        /// <summary>From the stairway, looking at the outer wall and the stairway floor.</summary>
        public static readonly (Vector3 eye, Vector3 at) Stairway = (new Vector3(0.8f, 3f, -5.5f), new Vector3(3f, 1.8f, 1.5f));

        public GameObject    Root;
        public GameObject    InnerWall, OuterWall;
        public VRSLLightZone Zone;

        /// <summary>The walls under <paramref name="parent"/>, and a zone (switched on, not
        /// yet assigned to anything) whose one box takes in the room and ends half way
        /// through the inner wall.</summary>
        public static VRSLZoneRoom Build(Transform parent, float thickness = WallThickness)
        {
            var room = new VRSLZoneRoom { Root = new GameObject("Zone room") };
            room.Root.transform.SetParent(parent, false);
            room.InnerWall = Wall(room.Root.transform, "Inner wall", thickness * 0.5f, thickness);
            room.OuterWall = Wall(room.Root.transform, "Outer wall", OuterWallX + 0.15f, 0.3f);

            var zoneGo = new GameObject("Room zone");
            zoneGo.transform.SetParent(room.Root.transform, false);
            room.Zone = zoneGo.AddComponent<VRSLLightZone>();
            var box = zoneGo.AddComponent<VRSLLightZoneBox>();
            float near = -12f, far = thickness * 0.5f;
            box.center = new Vector3((near + far) * 0.5f, 5.5f, 0f);
            box.size   = new Vector3(far - near, 13f, 34f);
            return room;
        }

        // Tall and wide enough that the room camera sees nothing past the inner wall.
        static GameObject Wall(Transform parent, string name, float x, float thickness)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            Object.DestroyImmediate(wall.GetComponent<Collider>());
            wall.transform.SetParent(parent, false);
            wall.transform.localPosition = new Vector3(x, 5f, 0f);
            wall.transform.localScale    = new Vector3(thickness, 10f, 30f);
            return wall;
        }

        /// <summary>The rig's camera moved to <paramref name="view"/> and captured in half
        /// float.</summary>
        public static Texture2D Capture(VRSLDMXRig rig, (Vector3 eye, Vector3 at) view)
        {
            rig.Camera.transform.SetPositionAndRotation(view.eye, Quaternion.LookRotation(view.at - view.eye, Vector3.up));
            return VRSLParBlinderBodyTests.CaptureHdr(rig);
        }

        /// <summary>The light in a frame, summed over every pixel and channel.</summary>
        public static double Energy(Texture2D frame)
        {
            double sum = 0;
            foreach (var p in frame.GetPixels()) sum += p.r + p.g + p.b;
            return sum;
        }

        /// <summary>The light <paramref name="with"/> holds over <paramref name="without"/>,
        /// pixel by pixel, for what one path alone adds.</summary>
        public static double Added(Texture2D with, Texture2D without)
        {
            var a = with.GetPixels();
            var b = without.GetPixels();
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
                sum += Mathf.Max(0f, a[i].r - b[i].r) + Mathf.Max(0f, a[i].g - b[i].g) + Mathf.Max(0f, a[i].b - b[i].b);
            return sum;
        }

        /// <summary>How far apart two frames are, pixel by pixel, summed.</summary>
        public static double Difference(Texture2D a, Texture2D b)
        {
            var pa = a.GetPixels();
            var pb = b.GetPixels();
            double sum = 0;
            for (int i = 0; i < pa.Length; i++)
                sum += Mathf.Abs(pa[i].r - pb[i].r) + Mathf.Abs(pa[i].g - pb[i].g) + Mathf.Abs(pa[i].b - pb[i].b);
            return sum;
        }

        /// <summary>
        /// The judgement every per-path row makes. Unzoned, the light reaches the stairway
        /// (or the row shows nothing); zoned, the stairway keeps under a hundredth of what
        /// reached it, and the room's frame matches its unzoned one to within
        /// <paramref name="roomTolerance"/> of the light in it, pixel by pixel.
        /// </summary>
        public static void Judge(string row, double stairsBefore, double stairsAfter,
                                 Texture2D roomBefore, Texture2D roomAfter, double roomLight,
                                 float roomTolerance)
        {
            double roomMoved = Difference(roomBefore, roomAfter);
            Debug.Log($"[{row}] stairway {stairsBefore:F1} unzoned, {stairsAfter:F2} zoned; room light {roomLight:F1}, "
                    + $"zoning moved it by {roomMoved:F2} ({100.0 * roomMoved / System.Math.Max(roomLight, 1e-9):F2}%)");
            Assert.Greater(roomLight, 1.0, $"{row}: the room isn't lit, so the row shows nothing");
            Assert.Greater(stairsBefore, roomLight * 0.01,
                $"{row}: unzoned, the light should reach the stairway through the wall (it added {stairsBefore:F2}); "
              + "without the leak the row shows nothing");
            Assert.Less(stairsAfter, stairsBefore * 0.01,
                $"{row}: zoned, the stairway still takes {stairsAfter:F2} of the {stairsBefore:F2} that reached it unzoned");
            Assert.Less(roomMoved, roomLight * roomTolerance,
                $"{row}: zoning changed the room by {roomMoved:F2} against {roomLight:F1} of light in it; "
              + "inside its zone a light should light as it did");
        }
    }

    /// <summary>
    /// LZ1 to LZ5 of TESTING.md: a DMX light zoned to the room keeps out of the stairway in
    /// surface light, beams, the disco ball's dots on surfaces and in the haze, and a gobo,
    /// and lights the room as it did.
    /// </summary>
    class VRSLLightZoneTests : VRSLDMXTest
    {
        const int ImageSize    = 256;
        const int WarmUpFrames = 4;
        const string BallPrefab = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/VRSL-DMX-Discoball-1CH.prefab";
        const string Cubemap    = "Packages/town.mr.vrsl-urp/Runtime/Textures/VRSL-DiscoBallCubeMap.png";
        /// <summary>The disco ball's channel, past the mover's thirteen.</summary>
        const int BallChannel = 14;

        /// <summary>The rig's one mover hung in the room and aimed at the inner wall, white
        /// at full on gobo slot <paramref name="goboSlot"/> (1 is the open beam), lit from
        /// a fixed universe. Surface light only unless <paramref name="beams"/>.</summary>
        internal static (VRSLDMXRig rig, VRSLZoneRoom room, VRSLFixedChannels source) Stage(
            bool beams, int goboSlot = 1, bool moverLit = true)
        {
            VRSLDMXRig rig = null;
            VRSLFixedChannels source = null;
            try
            {
                rig = VRSLDMXRig.Build(fixtures: 1, withSource: false, targetSize: ImageSize);
                rig.Manager.surfacePropertiesShader = null;
                rig.Manager.enabled = false;
                rig.Manager.enabled = true;
                rig.Manager.RefreshFixtures();
                rig.FreezeForImageCapture();
                if (!beams) rig.Manager.volumetricIntensity = 1e-12f;

                var mover = rig.Fixtures[0];
                var hung  = mover.transform;
                while (hung.parent != rig.Floor.transform.parent) hung = hung.parent;
                hung.SetPositionAndRotation(VRSLZoneRoom.LightAt, Quaternion.Euler(0f, 90f, 0f));
                mover.maxIntensity = moverLit ? 30f : 0f;
                mover.range        = 20f;

                var room = VRSLZoneRoom.Build(rig.Floor.transform.parent);

                var values = new float[26];
                values[5] = 1f;                        // dimmer
                values[7] = values[8] = values[9] = 1f; // white
                values[11] = GoboValue(goboSlot);
                values[BallChannel - 1] = 1f;
                source = new VRSLFixedChannels();
                source.Set(values);
                rig.Manager.ChannelSource = source;
                rig.Manager.SetDmxInput((int)VRSLDmxInput.StreamData);
                rig.Manager.MarkConfigDirty();
                return (rig, room, source);
            }
            catch
            {
                rig?.Dispose();
                source?.Dispose();
                throw;
            }
        }

        /// <summary>The middle of slot <paramref name="slot"/>'s DMX band (GW9).</summary>
        static float GoboValue(int slot) => slot <= 1 ? 0f : (30f * slot) / 255f;

        static VRSLDMXRealtimeLight HangBall(VRSLDMXRig rig, bool haze)
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BallPrefab);
            Assert.IsNotNull(prefab, $"the discoball prefab is not at {BallPrefab}");
            var go = Object.Instantiate(prefab, rig.Floor.transform.parent);
            go.name = "Discoball";
            go.transform.position = new Vector3(-3f, 4f, 0f);
            var ball = go.GetComponent<VRSLDMXRealtimeLight>();
            ball.useLegacySectorMode = false;
            ball.dmxUniverse  = 1;
            ball.dmxChannel   = BallChannel;
            ball.discoballSpinSpeed = 0f;
            ball.discoballBeams = haze;
            ball.maxIntensity = 30f;
            ball.range        = 20f;
            var cube = UnityEditor.AssetDatabase.LoadAssetAtPath<Cubemap>(Cubemap);
            Assert.IsNotNull(cube, $"the shipped cubemap is not at {Cubemap}");
            rig.Manager.discoballCubemap = cube;
            rig.Manager.RefreshFixtures();
            return ball;
        }

        internal static IEnumerator WarmUp(VRSLDMXRig rig)
        {
            for (int i = 0; i < WarmUpFrames; i++)
            {
                yield return null;
                rig.RenderFrame();
            }
        }

        /// <summary>Both views, beams on and (when <paramref name="beamsOnly"/>) again
        /// with the beams' intensity at nothing, so a row can take what the haze alone
        /// adds; and with the lights off, for what the lights add at all. The walls
        /// reflect the default sky whatever the lights do, so a frame's own sum is not
        /// what lit it.</summary>
        internal sealed class Views
        {
            public Texture2D Room, Stairs, RoomNoBeams, StairsNoBeams, RoomDark, StairsDark;

            public double RoomLight(bool beamsOnly, Views dark)  => beamsOnly ? VRSLZoneRoom.Added(Room, RoomNoBeams) : VRSLZoneRoom.Added(Room, dark.Room);
            public double StairLight(bool beamsOnly, Views dark) => beamsOnly ? VRSLZoneRoom.Added(Stairs, StairsNoBeams) : VRSLZoneRoom.Added(Stairs, dark.Stairs);

            public void Destroy()
            {
                foreach (var t in new[] { Room, Stairs, RoomNoBeams, StairsNoBeams, RoomDark, StairsDark })
                    if (t != null) Object.DestroyImmediate(t);
            }
        }

        internal static IEnumerator Take(VRSLDMXRig rig, System.Func<float> volumetric, System.Action<float> setVolumetric,
                                         bool beamsOnly, Views into)
        {
            for (int i = 0; i < WarmUpFrames; i++) { yield return null; rig.RenderFrame(); }
            into.Room   = VRSLZoneRoom.Capture(rig, VRSLZoneRoom.Room);
            into.Stairs = VRSLZoneRoom.Capture(rig, VRSLZoneRoom.Stairway);
            if (!beamsOnly) yield break;
            float was = volumetric();
            setVolumetric(1e-12f);
            yield return null;
            rig.RenderFrame();
            into.RoomNoBeams   = VRSLZoneRoom.Capture(rig, VRSLZoneRoom.Room);
            into.StairsNoBeams = VRSLZoneRoom.Capture(rig, VRSLZoneRoom.Stairway);
            setVolumetric(was);
        }

        /// <summary>The shape of LZ1 to LZ5: the stage captured unzoned, then with
        /// <paramref name="lights"/> on the room's zone.</summary>
        IEnumerator Row(string row, bool beams, bool beamsOnly, int goboSlot, bool ball, bool haze,
                        float roomTolerance)
        {
            var (rig, room, source) = Stage(beams, goboSlot, moverLit: !ball);
            var dark   = new Views();
            var before = new Views();
            var after  = new Views();
            try
            {
                var lights = ball ? new[] { HangBall(rig, haze) } : new[] { rig.Fixtures[0] };
                var m = rig.Manager;
                m.lightIntensity = 0f;
                m.MarkConfigDirty();
                yield return Take(rig, () => m.volumetricIntensity, v => m.volumetricIntensity = v, false, dark);
                m.lightIntensity = 1f;
                m.MarkConfigDirty();
                yield return Take(rig, () => m.volumetricIntensity, v => m.volumetricIntensity = v, beamsOnly, before);
                foreach (var light in lights) light.SetZone(room.Zone);
                yield return Take(rig, () => m.volumetricIntensity, v => m.volumetricIntensity = v, beamsOnly, after);

                VRSLZoneRoom.Judge(row, before.StairLight(beamsOnly, dark), after.StairLight(beamsOnly, dark),
                                   before.Room, after.Room, before.RoomLight(beamsOnly, dark), roomTolerance);
            }
            finally
            {
                dark.Destroy();
                before.Destroy();
                after.Destroy();
                rig.Dispose();
                source.Dispose();
            }
        }

        /// <summary>LZ1. Surface light.</summary>
        [UnityTest]
        public IEnumerator LZ1_a_zoned_light_keeps_its_surface_light_out_of_the_stairway()
            => Row("LZ1", beams: false, beamsOnly: false, goboSlot: 1, ball: false, haze: false, roomTolerance: 0.01f);

        /// <summary>LZ2. Beams, judged as what the haze adds.</summary>
        [UnityTest]
        public IEnumerator LZ2_a_zoned_light_keeps_its_beam_out_of_the_stairway()
            => Row("LZ2", beams: true, beamsOnly: true, goboSlot: 1, ball: false, haze: false, roomTolerance: 0.02f);

        /// <summary>LZ3. The disco ball's dots on surfaces.</summary>
        [UnityTest]
        public IEnumerator LZ3_a_zoned_disco_ball_keeps_its_dots_out_of_the_stairway()
            => Row("LZ3", beams: false, beamsOnly: false, goboSlot: 1, ball: true, haze: false, roomTolerance: 0.01f);

        /// <summary>LZ4. The disco ball's dots in the haze.</summary>
        [UnityTest]
        public IEnumerator LZ4_a_zoned_disco_ball_keeps_its_dots_in_the_haze_out_of_the_stairway()
            => Row("LZ4", beams: true, beamsOnly: true, goboSlot: 1, ball: true, haze: true, roomTolerance: 0.02f);

        /// <summary>LZ5. A gobo: the stairway dark, and the room showing the same gobo as
        /// unzoned, pixel by pixel.</summary>
        [UnityTest]
        public IEnumerator LZ5_a_zoned_light_keeps_its_gobo_out_of_the_stairway_and_on_the_wall()
            => Row("LZ5", beams: false, beamsOnly: false, goboSlot: 3, ball: false, haze: false, roomTolerance: 0.01f);
    }

    /// <summary>One universe of values a row chooses, as a desk would send them.</summary>
    sealed class VRSLFixedChannels : IVRSLDMXChannelSource, System.IDisposable
    {
        NativeArray<VRSLDMXBlock> _blocks = new(1, Allocator.Persistent);
        NativeArray<byte>         _values = new(VRSLDMX.UsableSlotsPerUniverse, Allocator.Persistent);

        public VRSLFixedChannels()
            => _blocks[0] = new VRSLDMXBlock { universe = 0, start = 0, length = VRSLDMX.UsableSlotsPerUniverse };

        public int UniverseCount => 1;

        /// <summary><paramref name="values"/>[0] is channel 1.</summary>
        public void Set(float[] values)
        {
            for (int i = 0; i < _values.Length; i++)
                _values[i] = (byte)Mathf.RoundToInt(255f * (i < values.Length ? values[i] : 0f));
        }

        public bool TryGetBlocks(out NativeArray<VRSLDMXBlock> blocks, out int blockCount,
                                 out NativeArray<byte> values)
        {
            blocks     = _blocks;
            values     = _values;
            blockCount = 1;
            return true;
        }

        public void Dispose()
        {
            if (_blocks.IsCreated) _blocks.Dispose();
            if (_values.IsCreated) _values.Dispose();
        }
    }
}
#endif
