#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows L11 and L13 of TESTING.md: the migration tool's sibling placement and field
    /// translation, driven with stand-ins for the original VRSL's DMX component (its field names,
    /// and its wash mover prefab's values as literals, since no test project has the original
    /// package). The tool's scene walk is L4/L5's.
    /// </summary>
    public class VRSLMigrationCopyTests
    {
        const string Wash = "Packages/town.mr.vrsl-urp/Runtime/Prefabs/DMX/VRSL-DMX-Mover-WashLight-13CH.prefab";

        // The original's VRStageLighting_DMX_Static, as its H 13CH wash mover prefab ships it.
        public class UpstreamDmxStatic : MonoBehaviour
        {
            public bool enableDMXChannels = true;
            public bool enableFineChannels;
            public int dmxChannel = 1;
            public int dmxUniverse = 1;
            public bool useLegacySectorMode;
            public int sector;
            public float globalIntensity = 1f;
            public float finalIntensity = 1f;
            public Color lightColorTint = new Color(2f, 2f, 2f, 2f);
            public bool invertPan = true;
            public bool invertTilt = true;
            public bool enableAutoSpin = true;
            public bool enableStrobe = true;
            public float tiltOffsetBlue = 90f;
            public float panOffsetBlueGreen;
            public float maxMinPan = 180f;
            public float maxMinTilt = -180f;
        }

        static readonly VRSLUpstreamTypes Types = new VRSLUpstreamTypes(
            "none", "none", "none", typeof(UpstreamDmxStatic).FullName, "none");

        GameObject _source, _sourceAsset, _sibling;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in new[] { _source, _sourceAsset, _sibling })
                if (go != null) Object.DestroyImmediate(go);
        }

        GameObject Place(System.Action<GameObject, UpstreamDmxStatic> setUp = null)
        {
            var urpAsset = AssetDatabase.LoadAssetAtPath<GameObject>(Wash);
            Assert.IsNotNull(urpAsset, Wash);

            _sourceAsset = new GameObject("Upstream wash prefab");
            _sourceAsset.AddComponent<UpstreamDmxStatic>();
            _source = new GameObject("Wash");
            var fixture = _source.AddComponent<UpstreamDmxStatic>();
            setUp?.Invoke(_source, fixture);

            var pair = new VRSLMigrationPair("upstream", "urp", "DMX H 13CH Mover Washlight", VRSLMigrationKind.Dmx);
            VRSLMigrationCopy.BeginRun();
            _sibling = VRSLMigrationCopy.PlaceSibling(_source.transform, _sourceAsset, urpAsset, pair, Types);
            Assert.IsNotNull(_sibling, "nothing was placed");
            return urpAsset;
        }

        /// <summary>
        /// L11. A sibling comes out as its original stands: switched off when the original is,
        /// on the original's layer and tag, and with its light switched off when the original's
        /// fixture component is. Otherwise a fixture the world kept dark (a spare, a room switched
        /// off at start) lights the scene once migrated, and cameras that leave a layer out see it.
        /// </summary>
        [Test]
        public void L11_a_sibling_takes_its_originals_state()
        {
            const int layer = 2;      // Ignore Raycast, built in
            const string tag = "Finish";
            Place((go, fixture) =>
            {
                go.layer = layer;
                go.tag = tag;
                fixture.enabled = false;
                go.SetActive(false);
            });

            Assert.IsFalse(_sibling.activeSelf, "the original is switched off, the sibling isn't");
            Assert.AreEqual(tag, _sibling.tag, "the sibling didn't take the original's tag");
            foreach (var t in _sibling.GetComponentsInChildren<Transform>(true))
                Assert.AreEqual(layer, t.gameObject.layer, $"'{t.name}' isn't on the original's layer");
            var light = _sibling.GetComponentInChildren<VRSLDMXRealtimeLight>(true);
            Assert.IsNotNull(light, "the wash prefab carries no DMX realtime light");
            Assert.IsFalse(light.enabled, "the original's fixture component is off, the sibling's light isn't");
        }

        /// <summary>
        /// L13. Settings that decide what a show does carry over as the original had them, even
        /// where this package's prefab differs: the original's wash pans inverted, ours doesn't,
        /// and a show programmed on the original expects the inverted pan. Its intensities, which
        /// are calibration, arrive at this package's values.
        /// </summary>
        [Test]
        public void L13_an_untouched_original_keeps_its_behaviour_and_takes_our_calibration()
        {
            var urpAsset = Place();
            var ours = urpAsset.GetComponentInChildren<VRSLDMXRealtimeLight>(true);
            var light = _sibling.GetComponentInChildren<VRSLDMXRealtimeLight>(true);

            Assert.IsFalse(ours.invertPan, "the shipped wash has started inverting pan; restate this row");
            Assert.IsTrue(light.invertPan, "the original's inverted pan didn't carry over");
            Assert.IsTrue(light.invertTilt, "the original's inverted tilt didn't carry over");
            Assert.AreEqual(-180f, light.maxMinTilt, "the original's tilt range didn't carry over");
            Assert.AreEqual(ours.globalIntensity, light.globalIntensity, 1e-6f, "global intensity isn't ours");
            Assert.AreEqual(ours.finalIntensity, light.finalIntensity, 1e-6f, "final intensity isn't ours");
            // Colour only: the managers and the body shaders don't read the tint's alpha.
            var tint = light.shellEmissionTint;
            var ourTint = ours.shellEmissionTint;
            Assert.AreEqual(new Vector3(ourTint.r, ourTint.g, ourTint.b), new Vector3(tint.r, tint.g, tint.b),
                "the tint isn't ours");
        }
    }
}
#endif
