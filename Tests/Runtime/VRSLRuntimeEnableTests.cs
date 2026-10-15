#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Realtime fixtures switched on and off while the world runs, with no call to
    /// <c>RefreshFixtures()</c>: the DMX manager follows them by itself, collecting once
    /// a frame however many switch and not at all in a frame where none did.
    ///
    /// Rows Z1 to Z6 of TESTING.md, DMX path. The AudioLink path is in
    /// <c>VRSLAudioLinkRuntimeEnableTests</c>.
    /// </summary>
    class VRSLRuntimeEnableTests : VRSLDMXTest
    {
        const string FixturePrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/VRSL-DMX-Mover-Spotlight-13CH.prefab";

        /// <summary>The prefab instance a rig fixture belongs to: what a world switches
        /// to hide the fixture.</summary>
        static GameObject InstanceOf(VRSLDMXRig rig, VRStageLighting_DMX_RealtimeLight f)
        {
            var root = rig.Floor.transform.parent;
            var t = f.transform;
            while (t.parent != null && t.parent != root) t = t.parent;
            return t.gameObject;
        }

        /// <summary>Whether the light data the last frame decoded holds a light at
        /// <paramref name="x"/>. Read by position because a re-collect reorders the
        /// slots, and with the buffer gone nothing is drawn at all.</summary>
        static bool Drawn(VRSLDMXRig rig, float x)
        {
            var m = rig.Manager;
            if (m.LightDataBuffer == null || m.FixtureCount == 0) return false;
            var data = new VRSL_URPLightManager.VRSLLightData[m.FixtureCount];
            m.LightDataBuffer.GetData(data);
            foreach (var d in data)
                if (Mathf.Abs(d.positionAndRange.x - x) < VRSLDMXRig.Spacing * 0.25f) return true;
            return false;
        }

        static void AssertHeld(VRSLDMXRig rig, int count, float x, bool drawn, string when)
        {
            Assert.AreEqual(count, rig.Manager.FixtureCount, $"fixtures the manager holds {when}");
            Assert.AreEqual(drawn, Drawn(rig, x),
                drawn ? $"the fixture should be drawn {when}" : $"the fixture should not be drawn {when}");
        }

        /// <summary>Row Z1. A fixture's GameObject switched off, then on.</summary>
        [UnityTest]
        public IEnumerator Z1_a_fixture_switched_off_and_on_is_followed()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                var f = rig.Fixtures[7];
                float x = f.transform.position.x;
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "before the switch");

                var go = InstanceOf(rig, f);
                go.SetActive(false);
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount - 1, x, false, "the frame after it was switched off");

                go.SetActive(true);
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "the frame after it was switched back on");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>Row Z2. The same through a parent the fixture sits under.</summary>
        [UnityTest]
        public IEnumerator Z2_a_fixture_under_a_parent_switched_off_and_on_is_followed()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                var f = rig.Fixtures[12];
                var go = InstanceOf(rig, f);
                var parent = new GameObject("Room");
                parent.transform.SetParent(go.transform.parent, false);
                go.transform.SetParent(parent.transform, true);
                yield return rig.Step(4);
                float x = f.transform.position.x;
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "before the switch");

                parent.SetActive(false);
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount - 1, x, false, "the frame after its parent was switched off");

                parent.SetActive(true);
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "the frame after its parent was switched back on");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>Row Z3. Seventy fixtures switched one by one in a single frame, the
        /// way a world's room culling does it: one scan and one rebuild that frame, and
        /// nothing in the frames after.</summary>
        [UnityTest]
        public IEnumerator Z3_seventy_switched_in_one_frame_are_collected_once()
        {
            const int Extra = 70;
            var rig = VRSLDMXRig.Build();
            try
            {
                var holder = new GameObject("Main room");
                holder.SetActive(false);
                holder.transform.SetParent(rig.Floor.transform.parent, false);
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(FixturePrefab);
                Assert.IsNotNull(prefab, FixturePrefab);
                var extra = new List<GameObject>();
                for (int i = 0; i < Extra; i++)
                {
                    var go = Object.Instantiate(prefab, holder.transform);
                    go.transform.localPosition = new Vector3(i * VRSLDMXRig.Spacing, 5.6f, 12f);
                    extra.Add(go);
                }
                yield return rig.Step(2);
                holder.SetActive(true);
                yield return rig.Step(3);
                Assert.AreEqual(VRSLDMXRig.FixtureCount + Extra, rig.Manager.FixtureCount,
                    "the room's fixtures, switched on with their parent, should be collected");

                int scans = rig.Manager.FixtureScans, rebuilds = rig.Manager.FixtureRebuilds;
                foreach (var go in extra) go.SetActive(false);
                yield return rig.Step(3);
                Assert.AreEqual(VRSLDMXRig.FixtureCount, rig.Manager.FixtureCount, "after seventy switched off");
                Assert.AreEqual(1, rig.Manager.FixtureScans - scans, "scans for seventy switched off in one frame");
                Assert.AreEqual(1, rig.Manager.FixtureRebuilds - rebuilds, "rebuilds for seventy switched off in one frame");

                scans = rig.Manager.FixtureScans; rebuilds = rig.Manager.FixtureRebuilds;
                foreach (var go in extra) go.SetActive(true);
                yield return rig.Step(3);
                Assert.AreEqual(VRSLDMXRig.FixtureCount + Extra, rig.Manager.FixtureCount, "after seventy switched on");
                Assert.AreEqual(1, rig.Manager.FixtureScans - scans, "scans for seventy switched on in one frame");
                Assert.AreEqual(1, rig.Manager.FixtureRebuilds - rebuilds, "rebuilds for seventy switched on in one frame");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>Row Z4. The last fixture switched off leaves the manager holding no
        /// fixture buffers; switched back on, it is drawn.</summary>
        [UnityTest]
        public IEnumerator Z4_the_last_fixture_off_releases_the_buffers()
        {
            var rig = VRSLDMXRig.Build(fixtures: 1);
            try
            {
                yield return rig.Step(4);
                var f = rig.Fixtures[0];
                float x = f.transform.position.x;
                AssertHeld(rig, 1, x, true, "before the switch");

                var go = InstanceOf(rig, f);
                go.SetActive(false);
                yield return rig.Step(1);
                Assert.AreEqual(0, rig.Manager.FixtureCount, "fixtures held with the last one off");
                Assert.IsNull(rig.Manager.LightDataBuffer, "light data held with no fixtures");
                Assert.IsNull(rig.Manager.FixtureConfigBuffer, "fixture configs held with no fixtures");

                go.SetActive(true);
                yield return rig.Step(1);
                AssertHeld(rig, 1, x, true, "the frame after it was switched back on");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>Row Z5. Frames in which nothing switched cost no scan and no rebuild,
        /// and neither does moving a fixture to a new address.</summary>
        [UnityTest]
        public IEnumerator Z5_quiet_frames_and_a_readdress_cost_no_scan()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                int scans = rig.Manager.FixtureScans, rebuilds = rig.Manager.FixtureRebuilds;
                var buffer = rig.Manager.LightDataBuffer;
                yield return rig.Step(10);
                Assert.AreEqual(scans, rig.Manager.FixtureScans, "scans in ten frames where nothing switched");
                Assert.AreEqual(rebuilds, rig.Manager.FixtureRebuilds, "rebuilds in ten frames where nothing switched");
                Assert.AreSame(buffer, rig.Manager.LightDataBuffer, "the light data was rebuilt with nothing switched");

                var f = rig.Fixtures[3];
                f.sector = 20;
                f.ApplyAddress();
                yield return rig.Step(2);
                Assert.AreEqual(scans, rig.Manager.FixtureScans, "scans after a fixture moved to a new address");
                Assert.AreEqual(rebuilds, rig.Manager.FixtureRebuilds, "rebuilds after a fixture moved to a new address");
                Assert.AreSame(buffer, rig.Manager.LightDataBuffer, "the light data was rebuilt after a fixture moved to a new address");
            }
            finally { rig.Dispose(); }
        }

        /// <summary>Row Z6. A fixture whose component is switched off, on a GameObject
        /// left on, stops being drawn; switched back on, it is drawn again.</summary>
        [UnityTest]
        public IEnumerator Z6_a_switched_off_component_is_not_drawn()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                yield return rig.Step(4);
                var f = rig.Fixtures[20];
                float x = f.transform.position.x;
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "before the switch");

                f.enabled = false;
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount - 1, x, false, "the frame after its component was switched off");

                f.enabled = true;
                yield return rig.Step(1);
                AssertHeld(rig, VRSLDMXRig.FixtureCount, x, true, "the frame after its component was switched back on");
            }
            finally { rig.Dispose(); }
        }
    }
}
#endif
