#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows Z1 to Z6 of TESTING.md on the AudioLink path: realtime fixtures switched on
    /// and off while the world runs, followed by the AudioLink manager with no call to
    /// <c>RefreshFixtures()</c>.
    ///
    /// The DMX rig stands beside the AudioLink manager for its camera and its quiet
    /// host; its one DMX fixture plays no part.
    /// </summary>
    class VRSLAudioLinkRuntimeEnableTests : VRSLDMXTest
    {
        const string ManagerPrefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-LightManager.prefab";
        const float FirstX  = 100f;
        const float Spacing = 2f;

        sealed class Stage : System.IDisposable
        {
            public VRSLDMXRig Rig;
            public GameObject Root;
            public VRSL_AudioLinkURPLightManager Manager;
            public readonly List<VRStageLighting_AudioLink_RealtimeLight> Fixtures = new();

            public void Dispose()
            {
                if (Root != null) Object.DestroyImmediate(Root);
                Rig?.Dispose();
            }
        }

        /// <summary>The fixtures first, so the manager's own enable collects them, then the
        /// manager.</summary>
        static Stage Build(int fixtures)
        {
            var stage = new Stage();
            try
            {
                stage.Rig  = VRSLDMXRig.Build(fixtures: 1, withSource: false);
                stage.Root = new GameObject("VRSL AudioLink runtime enable");
                for (int i = 0; i < fixtures; i++)
                {
                    var go = new GameObject($"AudioLink fixture ({i:000})");
                    go.transform.SetParent(stage.Root.transform, false);
                    go.transform.localPosition = new Vector3(FirstX + i * Spacing, 5.6f, 0f);
                    go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    stage.Fixtures.Add(go.AddComponent<VRStageLighting_AudioLink_RealtimeLight>());
                }
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ManagerPrefab);
                Assert.IsNotNull(prefab, ManagerPrefab);
                stage.Manager = Object.Instantiate(prefab, stage.Root.transform)
                                      .GetComponent<VRSL_AudioLinkURPLightManager>();
                Assert.AreSame(stage.Manager, VRSL_AudioLinkURPLightManager.Instance,
                               "the stage's AudioLink manager should own the singleton");
                return stage;
            }
            catch
            {
                stage.Dispose();
                throw;
            }
        }

        static bool Drawn(Stage s, float x)
        {
            var m = s.Manager;
            if (m.LightDataBuffer == null || m.FixtureCount == 0) return false;
            var data = new Vector4[m.FixtureCount * 4];
            m.LightDataBuffer.GetData(data);
            for (int i = 0; i < m.FixtureCount; i++)
                if (Mathf.Abs(data[i * 4].x - x) < Spacing * 0.25f) return true;
            return false;
        }

        static void AssertHeld(Stage s, int count, float x, bool drawn, string when)
        {
            Assert.AreEqual(count, s.Manager.FixtureCount, $"fixtures the manager holds {when}");
            Assert.AreEqual(drawn, Drawn(s, x),
                drawn ? $"the fixture should be drawn {when}" : $"the fixture should not be drawn {when}");
        }

        [UnityTest]
        public IEnumerator Z1_AudioLink_a_fixture_switched_off_and_on_is_followed()
        {
            var s = Build(10);
            try
            {
                yield return s.Rig.Step(3);
                var f = s.Fixtures[4];
                float x = f.transform.position.x;
                AssertHeld(s, 10, x, true, "before the switch");

                f.gameObject.SetActive(false);
                yield return s.Rig.Step(1);
                AssertHeld(s, 9, x, false, "the frame after it was switched off");

                f.gameObject.SetActive(true);
                yield return s.Rig.Step(1);
                AssertHeld(s, 10, x, true, "the frame after it was switched back on");
            }
            finally { s.Dispose(); }
        }

        [UnityTest]
        public IEnumerator Z2_AudioLink_a_fixture_under_a_parent_switched_off_and_on_is_followed()
        {
            var s = Build(10);
            try
            {
                var f = s.Fixtures[6];
                var parent = new GameObject("Room");
                parent.transform.SetParent(s.Root.transform, false);
                f.transform.SetParent(parent.transform, true);
                yield return s.Rig.Step(3);
                float x = f.transform.position.x;
                AssertHeld(s, 10, x, true, "before the switch");

                parent.SetActive(false);
                yield return s.Rig.Step(1);
                AssertHeld(s, 9, x, false, "the frame after its parent was switched off");

                parent.SetActive(true);
                yield return s.Rig.Step(1);
                AssertHeld(s, 10, x, true, "the frame after its parent was switched back on");
            }
            finally { s.Dispose(); }
        }

        [UnityTest]
        public IEnumerator Z3_AudioLink_seventy_switched_in_one_frame_are_collected_once()
        {
            const int Extra = 70;
            var s = Build(10);
            try
            {
                var holder = new GameObject("Main room");
                holder.SetActive(false);
                holder.transform.SetParent(s.Root.transform, false);
                var extra = new List<GameObject>();
                for (int i = 0; i < Extra; i++)
                {
                    var go = new GameObject($"Room fixture ({i:000})");
                    go.transform.SetParent(holder.transform, false);
                    go.transform.localPosition = new Vector3(FirstX + i * Spacing, 5.6f, 12f);
                    go.AddComponent<VRStageLighting_AudioLink_RealtimeLight>();
                    extra.Add(go);
                }
                yield return s.Rig.Step(2);
                holder.SetActive(true);
                yield return s.Rig.Step(3);
                Assert.AreEqual(10 + Extra, s.Manager.FixtureCount,
                    "the room's fixtures, switched on with their parent, should be collected");

                int scans = s.Manager.FixtureScans, rebuilds = s.Manager.FixtureRebuilds;
                foreach (var go in extra) go.SetActive(false);
                yield return s.Rig.Step(3);
                Assert.AreEqual(10, s.Manager.FixtureCount, "after seventy switched off");
                Assert.AreEqual(1, s.Manager.FixtureScans - scans, "scans for seventy switched off in one frame");
                Assert.AreEqual(1, s.Manager.FixtureRebuilds - rebuilds, "rebuilds for seventy switched off in one frame");

                scans = s.Manager.FixtureScans; rebuilds = s.Manager.FixtureRebuilds;
                foreach (var go in extra) go.SetActive(true);
                yield return s.Rig.Step(3);
                Assert.AreEqual(10 + Extra, s.Manager.FixtureCount, "after seventy switched on");
                Assert.AreEqual(1, s.Manager.FixtureScans - scans, "scans for seventy switched on in one frame");
                Assert.AreEqual(1, s.Manager.FixtureRebuilds - rebuilds, "rebuilds for seventy switched on in one frame");
            }
            finally { s.Dispose(); }
        }

        [UnityTest]
        public IEnumerator Z4_AudioLink_the_last_fixture_off_releases_the_buffers()
        {
            var s = Build(1);
            try
            {
                yield return s.Rig.Step(3);
                var f = s.Fixtures[0];
                float x = f.transform.position.x;
                AssertHeld(s, 1, x, true, "before the switch");

                f.gameObject.SetActive(false);
                yield return s.Rig.Step(1);
                Assert.AreEqual(0, s.Manager.FixtureCount, "fixtures held with the last one off");
                Assert.IsNull(s.Manager.LightDataBuffer, "light data held with no fixtures");
                Assert.IsNull(s.Manager.FixtureConfigBuffer, "fixture configs held with no fixtures");

                f.gameObject.SetActive(true);
                yield return s.Rig.Step(1);
                AssertHeld(s, 1, x, true, "the frame after it was switched back on");
            }
            finally { s.Dispose(); }
        }

        [UnityTest]
        public IEnumerator Z5_AudioLink_quiet_frames_cost_no_scan()
        {
            var s = Build(10);
            try
            {
                yield return s.Rig.Step(3);
                int scans = s.Manager.FixtureScans;
                var buffer = s.Manager.LightDataBuffer;
                yield return s.Rig.Step(10);
                Assert.AreEqual(scans, s.Manager.FixtureScans, "scans in ten frames where nothing switched");
                Assert.AreSame(buffer, s.Manager.LightDataBuffer, "the light data was rebuilt with nothing switched");
            }
            finally { s.Dispose(); }
        }

        [UnityTest]
        public IEnumerator Z6_AudioLink_a_switched_off_component_is_not_drawn()
        {
            var s = Build(10);
            try
            {
                yield return s.Rig.Step(3);
                var f = s.Fixtures[2];
                float x = f.transform.position.x;
                AssertHeld(s, 10, x, true, "before the switch");

                f.enabled = false;
                yield return s.Rig.Step(1);
                AssertHeld(s, 9, x, false, "the frame after its component was switched off");

                f.enabled = true;
                yield return s.Rig.Step(1);
                AssertHeld(s, 10, x, true, "the frame after its component was switched back on");
            }
            finally { s.Dispose(); }
        }
    }
}
#endif
