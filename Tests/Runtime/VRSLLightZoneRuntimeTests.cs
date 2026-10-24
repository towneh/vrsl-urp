#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// LZ8 to LZ14 and LZ19 to LZ21 of TESTING.md: zones of more than one box, zones and
    /// lights changed while the world runs, the zone data's cost on a frame where nothing
    /// changed, its fallback binding, how thin a wall a zone's face can sit in, and the
    /// keyword that selects the zone variants.
    /// </summary>
    class VRSLLightZoneRuntimeTests : VRSLDMXTest
    {
        const int WarmUpFrames = 4;

        static IEnumerator Frames(VRSLDMXRig rig, int n = WarmUpFrames)
        {
            for (int i = 0; i < n; i++) { yield return null; rig.RenderFrame(); }
        }

        static VRSLLightZoneBox AddBox(VRSLLightZone zone, string name, Vector3 min, Vector3 max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(zone.transform, false);
            var box = go.AddComponent<VRSLLightZoneBox>();
            box.center = (min + max) * 0.5f;
            box.size   = max - min;
            return box;
        }

        static VRSLLightZone NewZone(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.AddComponent<VRSLLightZone>();
        }

        /// <summary>Light in a window of pixels round where <paramref name="world"/> lands
        /// on the rig's camera, over the same window of <paramref name="without"/>.</summary>
        static double Window(VRSLDMXRig rig, Texture2D with, Texture2D without, Vector3 world, int half = 5)
        {
            var p = rig.Camera.WorldToScreenPoint(world);
            int cx = Mathf.RoundToInt(p.x), cy = Mathf.RoundToInt(p.y);
            var a = with.GetPixels();
            var b = without.GetPixels();
            double sum = 0;
            for (int y = Mathf.Max(0, cy - half); y <= Mathf.Min(with.height - 1, cy + half); y++)
                for (int x = Mathf.Max(0, cx - half); x <= Mathf.Min(with.width - 1, cx + half); x++)
                {
                    int i = y * with.width + x;
                    sum += Mathf.Max(0f, a[i].r - b[i].r) + Mathf.Max(0f, a[i].g - b[i].g) + Mathf.Max(0f, a[i].b - b[i].b);
                }
            return sum;
        }

        static void Aim(VRSLDMXRig rig, VRSLDMXRealtimeLight light, Vector3 at, Quaternion rotation)
        {
            var root = light.transform;
            while (root.parent != rig.Floor.transform.parent) root = root.parent;
            root.SetPositionAndRotation(at, rotation);
        }

        static Texture2D Shoot(VRSLDMXRig rig, Vector3 eye, Vector3 at)
            => VRSLZoneRoom.Capture(rig, (eye, at));

        // ── LZ8: more than one box ─────────────────────────────────────────────

        /// <summary>
        /// LZ8. A zone of two boxes with a gap between, in open air: a point light over the
        /// gap lights the floor in both boxes and not between; a beam marched down its
        /// length adds the haze of each box alone and nothing from the gap; and two boxes
        /// that overlap add the haze of the one box they make, not the overlap twice.
        /// </summary>
        [UnityTest]
        public IEnumerator LZ8_a_zone_of_several_boxes_lights_each_box_and_not_the_gap()
        {
            var rig = VRSLDMXRig.Build(fixtures: 2, withSource: false, targetSize: 256);
            VRSLFixedChannels source = null;
            var made = new System.Collections.Generic.List<Texture2D>();
            try
            {
                rig.Manager.surfacePropertiesShader = null;
                rig.Manager.enabled = false;
                rig.Manager.enabled = true;
                rig.Manager.RefreshFixtures();
                rig.FreezeForImageCapture();
                var beam  = rig.Fixtures[0];
                var point = rig.Fixtures[1];
                Aim(rig, beam, new Vector3(-4f, 3f, 0f), Quaternion.Euler(0f, 90f, 0f));
                beam.maxSpotAngle = 16f;
                beam.maxIntensity = 30f;
                Aim(rig, point, new Vector3(1f, 1.5f, 0f), Quaternion.Euler(90f, 0f, 0f));
                point.isPointLight = true;
                point.range        = 6f;
                point.maxIntensity = 6f;

                var values = new float[40];
                foreach (int abs in new[] { 1, 14 })
                {
                    values[abs + 4] = 1f;
                    values[abs + 6] = values[abs + 7] = values[abs + 8] = 1f;
                }
                source = new VRSLFixedChannels();
                source.Set(values);
                rig.Manager.ChannelSource = source;
                rig.Manager.SetDmxInput((int)VRSLDmxInput.StreamData);
                rig.Manager.MarkConfigDirty();

                var root = rig.Floor.transform.parent;
                var two = NewZone(root, "Two boxes");
                var boxA = AddBox(two, "A", new Vector3(-6f, -1f, -4f), new Vector3(0f, 7f, 4f));
                var boxB = AddBox(two, "B", new Vector3(2f, -1f, -4f), new Vector3(7f, 7f, 4f));
                var overlap = NewZone(root, "Overlapping boxes");
                AddBox(overlap, "A1", new Vector3(-6f, -1f, -4f), new Vector3(0f, 7f, 4f));
                AddBox(overlap, "A2", new Vector3(-3f, -1f, -4f), new Vector3(1f, 7f, 4f));
                var single = NewZone(root, "One box");
                AddBox(single, "A1 and A2 as one", new Vector3(-6f, -1f, -4f), new Vector3(1f, 7f, 4f));

                Vector3 sideEye = new(1f, 4f, -9f), sideAt = new(1f, 1.5f, 0f);
                Vector3 axisEye = new(-8f, 3.6f, -1.2f), axisAt = new(7f, 3f, 0f);
                var m = rig.Manager;

                // Each capture: the side view with beams off (the floor), the view down the
                // beam with beams on and off (the haze alone).
                Texture2D side = null, axisOn = null, axisOff = null;
                IEnumerator Shots()
                {
                    for (int i = 0; i < WarmUpFrames; i++) { yield return null; rig.RenderFrame(); }
                    m.volumetricIntensity = 1e-12f;
                    yield return null; rig.RenderFrame();
                    made.Add(side = Shoot(rig, sideEye, sideAt));
                    made.Add(axisOff = Shoot(rig, axisEye, axisAt));
                    m.volumetricIntensity = 1f;
                    yield return null; rig.RenderFrame();
                    made.Add(axisOn = Shoot(rig, axisEye, axisAt));
                }
                double Haze() => VRSLZoneRoom.Added(axisOn, axisOff);

                m.lightIntensity = 0f; m.MarkConfigDirty();
                yield return Shots();
                var darkSide = side;
                m.lightIntensity = 1f; m.MarkConfigDirty();

                yield return Shots();
                var openSide = side; double openHaze = Haze();

                beam.SetZone(two); point.SetZone(two);
                yield return Shots();
                var twoSide = side; double twoHaze = Haze();

                boxB.gameObject.SetActive(false);
                yield return Shots();
                double hazeA = Haze();
                boxB.gameObject.SetActive(true);
                boxA.gameObject.SetActive(false);
                yield return Shots();
                double hazeB = Haze();
                boxA.gameObject.SetActive(true);

                beam.SetZone(overlap); point.SetZone(overlap);
                yield return Shots();
                double overlapHaze = Haze();
                beam.SetZone(single); point.SetZone(single);
                yield return Shots();
                double singleHaze = Haze();

                Vector3 floorA = new(-1.5f, 0f, 0f), floorGap = new(1f, 0f, 0f), floorB = new(3.5f, 0f, 0f);
                rig.Camera.transform.SetPositionAndRotation(sideEye, Quaternion.LookRotation(sideAt - sideEye));
                double oA = Window(rig, openSide, darkSide, floorA), oG = Window(rig, openSide, darkSide, floorGap), oB = Window(rig, openSide, darkSide, floorB);
                double zA = Window(rig, twoSide, darkSide, floorA),  zG = Window(rig, twoSide, darkSide, floorGap),  zB = Window(rig, twoSide, darkSide, floorB);
                Debug.Log($"[LZ8] floor unzoned A {oA:F2} gap {oG:F2} B {oB:F2}; two boxes A {zA:F2} gap {zG:F2} B {zB:F2}");
                Debug.Log($"[LZ8] haze unzoned {openHaze:F1}, two boxes {twoHaze:F1}, A alone {hazeA:F1} + B alone {hazeB:F1} = {hazeA + hazeB:F1}; "
                        + $"overlapping pair {overlapHaze:F1}, the one box they make {singleHaze:F1}");

                Assert.Greater(oG, 1.0, "unzoned, the point light doesn't reach the floor between the boxes; the row shows nothing");
                Assert.Less(zG, oG * 0.02, "the floor between the two boxes is lit");
                Assert.AreEqual(oA, zA, oA * 0.02, "the floor in box A doesn't light as unzoned");
                Assert.AreEqual(oB, zB, oB * 0.02, "the floor in box B doesn't light as unzoned");
                Assert.Greater(hazeA, 1.0, "box A alone holds no haze");
                Assert.Greater(hazeB, 1.0, "box B alone holds no haze");
                Assert.Less(twoHaze, openHaze * 0.9, "the gap's haze is still there with the two-box zone");
                Assert.AreEqual(hazeA + hazeB, twoHaze, twoHaze * 0.1, "two boxes with a gap don't add up to each box alone");
                Assert.AreEqual(singleHaze, overlapHaze, singleHaze * 0.1, "two overlapping boxes don't light as the one box they make");
            }
            finally
            {
                foreach (var t in made) Object.DestroyImmediate(t);
                rig.Dispose();
                source?.Dispose();
            }
        }

        // ── LZ9: a zone changed while the world runs ───────────────────────────

        /// <summary>
        /// LZ9. The room's zone switched off (its light lights as unzoned) and on again; the
        /// zone moved to take in the stairway, with nothing told; its box resized by a
        /// script with <see cref="VRSLLightZone.Refresh"/>; its box switched off (the light
        /// lights nothing) and on.
        /// </summary>
        [UnityTest]
        public IEnumerator LZ9_a_zone_switched_moved_and_resized_while_the_world_runs()
        {
            var (rig, room, source) = VRSLLightZoneTests.Stage(beams: false);
            var dark = new VRSLLightZoneTests.Views();
            var shots = new System.Collections.Generic.List<VRSLLightZoneTests.Views>();
            try
            {
                var m = rig.Manager;
                var light = rig.Fixtures[0];
                IEnumerator Take(VRSLLightZoneTests.Views into)
                    => VRSLLightZoneTests.Take(rig, () => m.volumetricIntensity, v => m.volumetricIntensity = v, false, into);
                VRSLLightZoneTests.Views Next() { var v = new VRSLLightZoneTests.Views(); shots.Add(v); return v; }
                (double stairs, double room) Light(VRSLLightZoneTests.Views v) => (v.StairLight(false, dark), v.RoomLight(false, dark));

                m.lightIntensity = 0f; m.MarkConfigDirty();
                yield return Take(dark);
                m.lightIntensity = 1f; m.MarkConfigDirty();

                var open = Next();      yield return Take(open);
                light.SetZone(room.Zone);
                var zoned = Next();     yield return Take(zoned);
                room.Zone.enabled = false;
                var off = Next();       yield return Take(off);
                room.Zone.enabled = true;
                var on = Next();        yield return Take(on);
                room.Zone.transform.position += new Vector3(5f, 0f, 0f);
                var moved = Next();     yield return Take(moved);
                room.Zone.transform.position -= new Vector3(5f, 0f, 0f);
                var back = Next();      yield return Take(back);
                var box = room.Zone.GetComponent<VRSLLightZoneBox>();
                var size = box.size; var centre = box.center;
                box.size   += new Vector3(10f, 0f, 0f);
                box.center += new Vector3(5f, 0f, 0f);
                room.Zone.Refresh();
                var grown = Next();     yield return Take(grown);
                box.size = size; box.center = centre;
                room.Zone.Refresh();
                var shrunk = Next();    yield return Take(shrunk);
                box.enabled = false;
                var empty = Next();     yield return Take(empty);
                box.enabled = true;
                var refilled = Next();  yield return Take(refilled);

                var o = Light(open);
                foreach (var (name, v) in new[] { ("zoned", zoned), ("zone off", off), ("zone on", on), ("moved", moved),
                                                  ("moved back", back), ("grown", grown), ("shrunk", shrunk),
                                                  ("no box", empty), ("box on", refilled) })
                {
                    var l = Light(v);
                    Debug.Log($"[LZ9] {name}: stairway {l.stairs:F1}, room {l.room:F1} (unzoned {o.stairs:F1}, {o.room:F1})");
                }
                Assert.Greater(o.stairs, 1.0, "unzoned, nothing reaches the stairway; the row shows nothing");
                void Dark(VRSLLightZoneTests.Views v, string what) => Assert.Less(Light(v).stairs, o.stairs * 0.01, $"{what}: the stairway is lit");
                void Open(VRSLLightZoneTests.Views v, string what) => Assert.AreEqual(o.stairs, Light(v).stairs, o.stairs * 0.01, $"{what}: the stairway doesn't light as unzoned");
                void RoomLit(VRSLLightZoneTests.Views v, string what) => Assert.AreEqual(o.room, Light(v).room, o.room * 0.01, $"{what}: the room doesn't light as unzoned");
                Dark(zoned, "zoned");       RoomLit(zoned, "zoned");
                Open(off, "zone switched off");
                Dark(on, "zone switched on again"); RoomLit(on, "zone switched on again");
                Open(moved, "zone moved over the stairway");
                Dark(back, "zone moved back");
                Open(grown, "box grown over the stairway");
                Dark(shrunk, "box put back");
                Dark(empty, "zone with its box off");
                Assert.Less(Light(empty).room, o.room * 0.01, "with its only box off the zone should keep its light dark everywhere");
                Dark(refilled, "box on again"); RoomLit(refilled, "box on again");
            }
            finally
            {
                dark.Destroy();
                foreach (var v in shots) v.Destroy();
                rig.Dispose();
                source.Dispose();
            }
        }

        // ── LZ10 and LZ11: a light given another zone ──────────────────────────

        /// <summary>
        /// LZ10. A light moved from the room's zone to the stairway's lights only the
        /// stairway (through the wall, from outside its zone), and moved back lights only
        /// the room. LZ11. A light with no zone of its own takes the zone of a parent it is
        /// moved under, and loses it when moved out.
        /// </summary>
        [UnityTest]
        public IEnumerator LZ10_LZ11_a_light_moved_between_zones_and_under_a_parent_zone()
        {
            var (rig, room, source) = VRSLLightZoneTests.Stage(beams: false);
            var dark = new VRSLLightZoneTests.Views();
            var shots = new System.Collections.Generic.List<VRSLLightZoneTests.Views>();
            try
            {
                var m = rig.Manager;
                var light = rig.Fixtures[0];
                var stairs = NewZone(room.Root.transform, "Stairway zone");
                AddBox(stairs, "Stairway", new Vector3(VRSLZoneRoom.WallThickness * 0.5f, -1f, -17f),
                                           new Vector3(VRSLZoneRoom.OuterWallX + 0.15f, 12f, 17f));
                IEnumerator Take(VRSLLightZoneTests.Views into)
                    => VRSLLightZoneTests.Take(rig, () => m.volumetricIntensity, v => m.volumetricIntensity = v, false, into);
                VRSLLightZoneTests.Views Next() { var v = new VRSLLightZoneTests.Views(); shots.Add(v); return v; }
                (double stairs, double room) Light(VRSLLightZoneTests.Views v) => (v.StairLight(false, dark), v.RoomLight(false, dark));

                m.lightIntensity = 0f; m.MarkConfigDirty();
                yield return Take(dark);
                m.lightIntensity = 1f; m.MarkConfigDirty();

                var open = Next();    yield return Take(open);
                light.SetZone(room.Zone);
                var inRoom = Next();  yield return Take(inRoom);
                light.SetZone(stairs);
                var inStairs = Next(); yield return Take(inStairs);
                light.SetZone(room.Zone);
                var back = Next();    yield return Take(back);

                // LZ11: no zone of its own, then under the stairway zone's GameObject.
                light.SetZone(null);
                var none = Next();    yield return Take(none);
                var hung = light.transform;
                while (hung.parent != rig.Floor.transform.parent) hung = hung.parent;
                hung.SetParent(stairs.transform, true);
                var parented = Next(); yield return Take(parented);
                hung.SetParent(rig.Floor.transform.parent, true);
                var unparented = Next(); yield return Take(unparented);

                var o = Light(open);
                foreach (var (name, v) in new[] { ("room zone", inRoom), ("stairway zone", inStairs), ("room zone again", back),
                                                  ("no zone", none), ("under the stairway zone", parented), ("out from under it", unparented) })
                {
                    var l = Light(v);
                    Debug.Log($"[LZ10] {name}: stairway {l.stairs:F1}, room {l.room:F1} (unzoned {o.stairs:F1}, {o.room:F1})");
                }
                Assert.Greater(o.stairs, 1.0, "unzoned, nothing reaches the stairway; the row shows nothing");
                Assert.Less(Light(inRoom).stairs, o.stairs * 0.01, "in the room's zone the stairway is lit");
                Assert.AreEqual(o.room, Light(inRoom).room, o.room * 0.01, "in the room's zone the room doesn't light as unzoned");
                Assert.AreEqual(o.stairs, Light(inStairs).stairs, o.stairs * 0.01, "moved to the stairway's zone, the stairway doesn't light as unzoned");
                Assert.Less(Light(inStairs).room, o.room * 0.01, "moved to the stairway's zone, the room is still lit");
                Assert.Less(Light(back).stairs, o.stairs * 0.01, "moved back to the room's zone, the stairway is lit");
                Assert.AreEqual(o.room, Light(back).room, o.room * 0.01, "moved back to the room's zone, the room doesn't light as unzoned");
                Assert.AreEqual(o.stairs, Light(none).stairs, o.stairs * 0.01, "with no zone the stairway doesn't light as unzoned");
                Assert.Less(Light(parented).room, o.room * 0.01, "under the stairway zone's GameObject the light should take that zone");
                Assert.AreEqual(o.room, Light(unparented).room, o.room * 0.01, "moved out from under the zone the light should be unzoned again");
            }
            finally
            {
                dark.Destroy();
                foreach (var v in shots) v.Destroy();
                rig.Dispose();
                source.Dispose();
            }
        }

        // ── LZ19 and LZ20: reparented higher up ────────────────────────────────

        /// <summary>
        /// LZ19. A light with no zone of its own, under a rig GameObject: the rig moved under
        /// the stairway zone's GameObject and out again, the light's own parent unchanged.
        /// </summary>
        [UnityTest]
        public IEnumerator LZ19_a_light_takes_the_zone_its_rig_is_moved_under()
        {
            var (rig, room, source) = VRSLLightZoneTests.Stage(beams: false);
            var dark = new VRSLLightZoneTests.Views();
            var shots = new System.Collections.Generic.List<VRSLLightZoneTests.Views>();
            try
            {
                var m = rig.Manager;
                var light = rig.Fixtures[0];
                var stairs = NewZone(room.Root.transform, "Stairway zone");
                AddBox(stairs, "Stairway", new Vector3(VRSLZoneRoom.WallThickness * 0.5f, -1f, -17f),
                                           new Vector3(VRSLZoneRoom.OuterWallX + 0.15f, 12f, 17f));
                var top = rig.Floor.transform.parent;
                var hung = light.transform;
                while (hung.parent != top) hung = hung.parent;
                var truss = new GameObject("Truss").transform;
                truss.SetParent(top, false);
                hung.SetParent(truss, true);
                IEnumerator Take(VRSLLightZoneTests.Views into)
                    => VRSLLightZoneTests.Take(rig, () => m.volumetricIntensity, v => m.volumetricIntensity = v, false, into);
                VRSLLightZoneTests.Views Next() { var v = new VRSLLightZoneTests.Views(); shots.Add(v); return v; }
                (double stairs, double room) Light(VRSLLightZoneTests.Views v) => (v.StairLight(false, dark), v.RoomLight(false, dark));

                m.lightIntensity = 0f; m.MarkConfigDirty();
                yield return Take(dark);
                m.lightIntensity = 1f; m.MarkConfigDirty();

                var open = Next();       yield return Take(open);
                truss.SetParent(stairs.transform, true);
                var under = Next();      yield return Take(under);
                truss.SetParent(top, true);
                var outAgain = Next();   yield return Take(outAgain);

                var o = Light(open);
                foreach (var (name, v) in new[] { ("rig under the stairway zone", under), ("rig out again", outAgain) })
                {
                    var l = Light(v);
                    Debug.Log($"[LZ19] {name}: stairway {l.stairs:F1}, room {l.room:F1} (unzoned {o.stairs:F1}, {o.room:F1})");
                }
                Assert.Greater(o.room, 1.0, "unzoned, nothing reaches the room; the row shows nothing");
                Assert.Less(Light(under).room, o.room * 0.01, "with its rig under the stairway zone's GameObject the light should take that zone");
                Assert.AreEqual(o.room, Light(outAgain).room, o.room * 0.01, "with its rig moved out again the light should be unzoned");
            }
            finally
            {
                dark.Destroy();
                foreach (var v in shots) v.Destroy();
                rig.Dispose();
                source.Dispose();
            }
        }

        /// <summary>
        /// LZ20. LZ1's light zoned to the room, whose only live box sits two GameObjects
        /// below the zone: the box moved out of the zone and back, then the GameObject
        /// holding it moved out and back, with nothing told.
        /// </summary>
        [UnityTest]
        public IEnumerator LZ20_a_box_moved_out_of_its_zone_stops_counting()
        {
            var (rig, room, source) = VRSLLightZoneTests.Stage(beams: false);
            var dark = new VRSLLightZoneTests.Views();
            var shots = new System.Collections.Generic.List<VRSLLightZoneTests.Views>();
            try
            {
                var m = rig.Manager;
                var light = rig.Fixtures[0];
                var own = room.Zone.GetComponent<VRSLLightZoneBox>();
                var group = new GameObject("Boxes").transform;
                group.SetParent(room.Zone.transform, false);
                var holder = new GameObject("Holder").transform;
                holder.SetParent(group, false);
                var nested = holder.gameObject.AddComponent<VRSLLightZoneBox>();
                nested.center = own.center; nested.size = own.size;
                own.enabled = false;
                light.SetZone(room.Zone);
                var top = rig.Floor.transform.parent;
                IEnumerator Take(VRSLLightZoneTests.Views into)
                    => VRSLLightZoneTests.Take(rig, () => m.volumetricIntensity, v => m.volumetricIntensity = v, false, into);
                VRSLLightZoneTests.Views Next() { var v = new VRSLLightZoneTests.Views(); shots.Add(v); return v; }
                (double stairs, double room) Light(VRSLLightZoneTests.Views v) => (v.StairLight(false, dark), v.RoomLight(false, dark));

                m.lightIntensity = 0f; m.MarkConfigDirty();
                yield return Take(dark);
                m.lightIntensity = 1f; m.MarkConfigDirty();

                var zoned = Next();      yield return Take(zoned);
                holder.SetParent(top, true);
                var boxOut = Next();     yield return Take(boxOut);
                holder.SetParent(group, true);
                var boxBack = Next();    yield return Take(boxBack);
                var inner = new GameObject("Inner").transform;
                inner.SetParent(holder.parent, false);
                holder.SetParent(inner, true);
                yield return Take(Next());
                inner.SetParent(top, true);
                var parentOut = Next();  yield return Take(parentOut);
                inner.SetParent(group, true);
                var parentBack = Next(); yield return Take(parentBack);

                var z = Light(zoned);
                foreach (var (name, v) in new[] { ("box moved out", boxOut), ("box back", boxBack),
                                                  ("its parent moved out", parentOut), ("its parent back", parentBack) })
                {
                    var l = Light(v);
                    Debug.Log($"[LZ20] {name}: stairway {l.stairs:F1}, room {l.room:F1} (zoned {z.stairs:F1}, {z.room:F1})");
                }
                Assert.Greater(z.room, 1.0, "zoned through the nested box, the room is dark; the row shows nothing");
                Assert.Less(Light(boxOut).room, z.room * 0.01, "with its only live box moved out of the zone the light should light nothing");
                Assert.AreEqual(z.room, Light(boxBack).room, z.room * 0.01, "with the box moved back the room should light as before");
                Assert.Less(Light(parentOut).room, z.room * 0.01, "with the box's parent moved out of the zone the light should light nothing");
                Assert.AreEqual(z.room, Light(parentBack).room, z.room * 0.01, "with the box's parent moved back the room should light as before");
            }
            finally
            {
                dark.Destroy();
                foreach (var v in shots) v.Destroy();
                rig.Dispose();
                source.Dispose();
            }
        }

        // ── LZ12: nothing per frame ────────────────────────────────────────────

        /// <summary>
        /// LZ12. With zones in the scene, a frame in which nothing changed uploads no zone
        /// data, binds nothing and allocates nothing in the registry; moving the zone
        /// uploads once.
        /// </summary>
        [UnityTest]
        public IEnumerator LZ12_a_frame_with_nothing_changed_uploads_binds_and_allocates_nothing()
        {
            var (rig, room, source) = VRSLLightZoneTests.Stage(beams: true);
            try
            {
                var second = NewZone(room.Root.transform, "Second zone");
                AddBox(second, "One", new Vector3(5f, 0f, 0f), new Vector3(6f, 1f, 1f));
                AddBox(second, "Two", new Vector3(7f, 0f, 0f), new Vector3(8f, 1f, 1f));
                rig.Fixtures[0].SetZone(room.Zone);
                yield return rig.Step(4);
                Assert.AreEqual(2, VRSLLightZones.Count, "the row's two zones aren't both switched on");

                int uploads = VRSLLightZones.Uploads, binds = VRSLLightZones.Binds;
                int allocatingFrames = 0;
                const int frames = 30;
                for (int i = 0; i < frames; i++)
                {
                    yield return null;
                    // First in the frame, so this call does the frame's work rather than
                    // finding it done.
                    try { Assert.That(() => VRSLSceneResources.Prepare(), Is.Not.AllocatingGCMemory()); }
                    catch (AssertionException) { allocatingFrames++; }
                    rig.RenderFrame();
                }
                Debug.Log($"[LZ12] over {frames} frames: {VRSLLightZones.Uploads - uploads} uploads, "
                        + $"{VRSLLightZones.Binds - binds} binds, {allocatingFrames} frames allocating");
                Assert.AreEqual(uploads, VRSLLightZones.Uploads, "a frame with nothing changed uploaded zone data");
                Assert.AreEqual(binds, VRSLLightZones.Binds, "a frame with nothing changed bound the zone data");
                Assert.AreEqual(0, allocatingFrames, "the zone registry allocated on a frame with nothing changed");

                second.transform.position += Vector3.up;
                yield return rig.Step(2);
                Assert.AreEqual(uploads + 1, VRSLLightZones.Uploads, "moving a zone should upload its data once");
            }
            finally
            {
                rig.Dispose();
                source.Dispose();
            }
        }

        // ── LZ13: the fallback binding ─────────────────────────────────────────

        /// <summary>
        /// LZ13, in N35's shape, on D3D12 and Vulkan: the room lit with no zone ever on, and
        /// again after a zone came and went. Both fullscreen passes declare the zone buffer,
        /// so with it unbound D3D12 and Vulkan skip them and nothing lights; D3D11 reads an
        /// unbound buffer as zero and draws, so the row passes there whatever the binding.
        /// </summary>
        [UnityTest]
        public IEnumerator LZ13_the_lights_draw_with_no_zone_on()
        {
            var (rig, room, source) = VRSLLightZoneTests.Stage(beams: true);
            var dark = new VRSLLightZoneTests.Views();
            var never = new VRSLLightZoneTests.Views();
            var after = new VRSLLightZoneTests.Views();
            try
            {
                var m = rig.Manager;
                Object.DestroyImmediate(room.Zone.gameObject);
                IEnumerator Take(VRSLLightZoneTests.Views into)
                    => VRSLLightZoneTests.Take(rig, () => m.volumetricIntensity, v => m.volumetricIntensity = v, false, into);
                m.lightIntensity = 0f; m.MarkConfigDirty();
                yield return Take(dark);
                m.lightIntensity = 1f; m.MarkConfigDirty();
                Assert.AreEqual(0, VRSLLightZones.Count, "a zone is switched on");
                yield return Take(never);

                var zone = NewZone(room.Root.transform, "Zone that comes and goes");
                AddBox(zone, "Box", Vector3.zero, Vector3.one);
                yield return Frames(rig);
                Object.DestroyImmediate(zone.gameObject);
                yield return Take(after);

                double lit = never.RoomLight(false, dark), litAfter = after.RoomLight(false, dark);
                Debug.Log($"[LZ13] {SystemInfo.graphicsDeviceType}: room light {lit:F1} with no zone ever, {litAfter:F1} after one came and went");
                Assert.Greater(lit, 100.0, $"on {SystemInfo.graphicsDeviceType} nothing lights with no zone on: the passes were skipped");
                Assert.AreEqual(lit, litAfter, lit * 0.01, $"on {SystemInfo.graphicsDeviceType} the lights changed after a zone came and went");
            }
            finally
            {
                dark.Destroy(); never.Destroy(); after.Destroy();
                rig.Dispose();
                source.Dispose();
            }
        }

        // ── LZ21: the zone keyword ─────────────────────────────────────────────

        /// <summary>
        /// LZ21. The fullscreen shaders' zone variants are selected only while a zone is on:
        /// off with none, on as soon as one joins, and off again within a few frames of the
        /// last one leaving, read from the global keyword itself.
        /// </summary>
        [UnityTest]
        public IEnumerator LZ21_the_zone_variants_run_only_while_a_zone_is_on()
        {
            var (rig, room, source) = VRSLLightZoneTests.Stage(beams: true);
            try
            {
                Object.DestroyImmediate(room.Zone.gameObject);
                yield return Frames(rig);
                Assert.AreEqual(0, VRSLLightZones.Count, "a zone is switched on");
                Assert.IsFalse(Shader.IsKeywordEnabled("VRSL_LIGHT_ZONES"), "with no zone on, the zone variants are selected");

                var zone = NewZone(room.Root.transform, "Zone that comes and goes");
                AddBox(zone, "Box", Vector3.zero, Vector3.one);
                Assert.IsTrue(Shader.IsKeywordEnabled("VRSL_LIGHT_ZONES"), "a zone came on and the zone variants aren't selected");
                yield return Frames(rig);
                Assert.IsTrue(Shader.IsKeywordEnabled("VRSL_LIGHT_ZONES"), "with a zone on, the zone variants were let go");

                Object.DestroyImmediate(zone.gameObject);
                yield return Frames(rig);
                Assert.IsFalse(Shader.IsKeywordEnabled("VRSL_LIGHT_ZONES"), "the last zone left and the zone variants are still selected");
            }
            finally
            {
                rig.Dispose();
                source.Dispose();
            }
        }

        // ── LZ14: wall thickness ───────────────────────────────────────────────

        /// <summary>
        /// LZ14. The inner wall at 5 cm, 2 cm and 1 cm with the zone's face half way
        /// through it, seen from 8.5 m and from 20 m: the near face lights as unzoned and
        /// the stairway stays dark at each. The thinnest that holds is the figure the docs
        /// give.
        /// </summary>
        [UnityTest]
        public IEnumerator LZ14_a_zone_face_inside_a_thin_wall_keeps_the_near_face_lit()
        {
            foreach (float t in new[] { 0.05f, 0.02f, 0.01f })
            {
                var (rig, room, source) = VRSLLightZoneTests.Stage(beams: false);
                var shots = new System.Collections.Generic.List<Texture2D>();
                try
                {
                    var m = rig.Manager;
                    Object.DestroyImmediate(room.Root);
                    room = VRSLZoneRoom.Build(rig.Floor.transform.parent, t);
                    var near = (VRSLZoneRoom.Room.eye, VRSLZoneRoom.Room.at);
                    var far  = (new Vector3(-19f, 3f, -10f), new Vector3(0f, 3f, 0f));

                    Texture2D Shot((Vector3, Vector3) v) { var f = VRSLZoneRoom.Capture(rig, v); shots.Add(f); return f; }
                    m.lightIntensity = 0f; m.MarkConfigDirty();
                    yield return Frames(rig);
                    var darkNear = Shot(near); var darkFar = Shot(far); var darkStairs = Shot(VRSLZoneRoom.Stairway);
                    m.lightIntensity = 1f; m.MarkConfigDirty();
                    yield return Frames(rig);
                    var openNear = Shot(near); var openFar = Shot(far); var openStairs = Shot(VRSLZoneRoom.Stairway);
                    rig.Fixtures[0].SetZone(room.Zone);
                    yield return Frames(rig);
                    var zoneNear = Shot(near); var zoneFar = Shot(far); var zoneStairs = Shot(VRSLZoneRoom.Stairway);

                    double nearLight = VRSLZoneRoom.Added(openNear, darkNear), farLight = VRSLZoneRoom.Added(openFar, darkFar);
                    double nearMoved = VRSLZoneRoom.Difference(openNear, zoneNear), farMoved = VRSLZoneRoom.Difference(openFar, zoneFar);
                    double leak = VRSLZoneRoom.Added(openStairs, darkStairs), kept = VRSLZoneRoom.Added(zoneStairs, darkStairs);
                    Debug.Log($"[LZ14] wall {t * 100f:0} cm: near face from 8.5 m moved {100 * nearMoved / nearLight:F3}%, "
                            + $"from 20 m {100 * farMoved / farLight:F3}%; stairway {kept:F2} of {leak:F1}");
                    Assert.Less(nearMoved, nearLight * 0.01, $"a {t * 100f:0} cm wall: the near face from 8.5 m lost light to the zone");
                    Assert.Less(farMoved, farLight * 0.01, $"a {t * 100f:0} cm wall: the near face from 20 m lost light to the zone");
                    Assert.Less(kept, leak * 0.01, $"a {t * 100f:0} cm wall: the stairway is lit");
                }
                finally
                {
                    foreach (var f in shots) Object.DestroyImmediate(f);
                    rig.Dispose();
                    source.Dispose();
                }
            }
        }
    }
}
#endif
