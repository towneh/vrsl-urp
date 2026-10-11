using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The AudioLink realtime light and manager on their own: the body glow's theme
    /// colour, and the per-frame upload surviving a fixture destroyed mid-session.
    ///
    /// Rows C9 and C10 of TESTING.md.
    /// </summary>
    class VRSLAudioLinkFixtureTests
    {
        GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        /// <summary>
        /// Row C9. The body shader numbers AudioLink's four theme colours 1 to 4 and draws
        /// black for anything else, so each theme mode has to reach the body as its own
        /// number: ThemeColor0 as 1, ThemeColor3 as 4.
        /// </summary>
        [Test]
        public void C9_each_theme_colour_reaches_the_body_as_the_shader_numbers_it()
        {
            _go = new GameObject("vrsl-audiolink-theme");
            _go.SetActive(false);
            var light = _go.AddComponent<VRStageLighting_AudioLink_RealtimeLight>();
            var body  = _go.AddComponent<MeshRenderer>();
            light.fixtureShellRenderers = new[] { body };

            var block = new MaterialPropertyBlock();
            for (int n = 0; n < 4; n++)
            {
                light.colorMode = ALRealtimeColorMode.ThemeColor0 + n;
                light.DriveFixtureShells();
                body.GetPropertyBlock(block);
                Assert.AreEqual(n + 1, (int)block.GetFloat("_ThemeColorTarget"),
                                $"ThemeColor{n} should reach the body as theme colour {n + 1}");
                Assert.AreEqual(1, (int)block.GetFloat("_EnableThemeColorSampling"));
            }
        }

        /// <summary>
        /// Row C10. A fixture destroyed while the manager runs, with no RefreshFixtures()
        /// after it, must leave its slot dark rather than throw from the per-frame upload,
        /// which runs inside camera rendering and would stop every AudioLink pass.
        /// </summary>
        [Test]
        public void C10_a_destroyed_fixture_goes_dark_instead_of_stopping_the_upload()
        {
            _go = new GameObject("vrsl-audiolink-upload");
            var fixtureGo = new GameObject("fixture");
            fixtureGo.transform.SetParent(_go.transform);
            var light = fixtureGo.AddComponent<VRStageLighting_AudioLink_RealtimeLight>();
            light.finalIntensity = 1f;

            var manager = _go.AddComponent<VRSL_AudioLinkURPLightManager>();
            try
            {
                manager.RefreshFixtures();
                Assume.That(manager.FixtureCount, Is.EqualTo(1),
                            "the manager should have collected the one fixture");
                Assume.That(manager.FixtureConfigBuffer, Is.Not.Null,
                            "the manager should have allocated its config buffer");

                Object.DestroyImmediate(fixtureGo);
                var upload = typeof(VRSL_AudioLinkURPLightManager).GetMethod(
                    "UploadFixtureConfigs", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(upload, "the manager's per-frame upload");
                Assert.DoesNotThrow(() => upload.Invoke(manager, null),
                                    "a destroyed fixture must not stop the upload");

                var configs = new VRSL_AudioLinkURPLightManager.VRSLALFixtureConfig[1];
                manager.FixtureConfigBuffer.GetData(configs);
                Assert.AreEqual(0f, configs[0].intensityParams.y, "the slot is dark");
                Assert.AreEqual(1f, configs[0].forwardAndType.magnitude, 1e-5f,
                                "with a direction the compute can normalise");
            }
            finally
            {
                Object.DestroyImmediate(_go);
            }
        }
    }
}
