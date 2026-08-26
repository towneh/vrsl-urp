using NUnit.Framework;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows for which managers the quality preset actually reaches.
    ///
    /// The managers are built on an inactive GameObject on purpose. Nothing here needs
    /// them to run: it needs their fields. Inactive means `Awake` and `OnEnable` never
    /// fire, `VolumetricMaterial` stays null, and the preset's `Bounce` — which toggles
    /// `enabled` — is inert, so a row cannot start a light manager as a side effect of
    /// asking what a preset writes.
    ///
    /// Row H8 of TESTING.md.
    /// </summary>
    class VRSLQualityPresetTests
    {
        GameObject _go;
        Shader     _shader;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("vrsl-quality-preset-target");
            _go.SetActive(false);

            _shader = Shader.Find("Hidden/VRSL-URP/VolumetricLighting");
            // Asserted rather than ignored. A row that skips when the package's own
            // shader cannot be found reads green on a project where the package is
            // broken, which is the reverse of what it is for.
            Assert.IsNotNull(_shader, "the package's volumetric shader should be findable "
                                    + "in a project that has the package");
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [Test]
        public void AnAudioLinkOnlySceneStillGetsItsLevelsApplied()
        {
            var manager = _go.AddComponent<VRSL_AudioLinkURPLightManager>();
            manager.volumetricShader     = _shader;
            manager.volumetricStepCount  = 7;
            manager.contactShadowSteps   = 3;

            var session = VRSLQualityPreset.Session.Begin(null, manager);

            session.Apply(VRSLQualityPreset.Level.Off);
            Assert.IsNull(manager.volumetricShader,
                "Off has to clear the shader as well as the material, or the next OnEnable "
              + "rebuilds one straight off it");

            session.Apply(VRSLQualityPreset.Level.Standard);
            Assert.AreEqual(_shader, manager.volumetricShader, "coming back from Off restores the shader");
            Assert.AreEqual(24, manager.volumetricStepCount);
            Assert.AreEqual(8,  manager.contactShadowSteps);

            session.Apply(VRSLQualityPreset.Level.High);
            Assert.AreEqual(40, manager.volumetricStepCount);
            Assert.AreEqual(16, manager.contactShadowSteps);

            session.Restore();
            Assert.AreEqual(_shader, manager.volumetricShader);
            Assert.AreEqual(7, manager.volumetricStepCount, "the author's own value goes back");
            Assert.AreEqual(3, manager.contactShadowSteps);
        }

        [Test]
        public void ASceneWithBothManagersHoldsBothAtTheLevel()
        {
            // The case that makes an Off capture meaningless when it is missed: quality
            // held on one manager leaves the other's volumetrics running at every level,
            // so the split between beams and surface lighting is measured against a
            // baseline that still has beams in it.
            var dmx       = _go.AddComponent<VRSL_URPLightManager>();
            var audioLink = _go.AddComponent<VRSL_AudioLinkURPLightManager>();
            dmx.volumetricShader       = _shader;
            audioLink.volumetricShader = _shader;

            var session = VRSLQualityPreset.Session.Begin(dmx, audioLink);

            session.Apply(VRSLQualityPreset.Level.Off);
            Assert.IsNull(dmx.volumetricShader,       "the DMX manager is held at the level");
            Assert.IsNull(audioLink.volumetricShader, "and so is the AudioLink one");

            session.Apply(VRSLQualityPreset.Level.High);
            Assert.AreEqual(40, dmx.volumetricStepCount);
            Assert.AreEqual(40, audioLink.volumetricStepCount);

            session.Restore();
            Assert.AreEqual(_shader, dmx.volumetricShader);
            Assert.AreEqual(_shader, audioLink.volumetricShader);
        }

        [Test]
        public void StrobeIsHeldOnWhereTheManagerHasThatSwitch()
        {
            // DMX strobing alternates fixture by fixture, so a capture taken without this
            // measures a random subset of the rig. The AudioLink path strobes off the
            // audio and has no such switch, which is why the preset treats it as absent
            // rather than faking one.
            var dmx = _go.AddComponent<VRSL_URPLightManager>();
            dmx.volumetricShader = _shader;
            dmx.disableStrobe    = false;

            var session = VRSLQualityPreset.Session.Begin(dmx);
            Assert.IsTrue(dmx.disableStrobe, "Begin holds every strobing fixture on");

            session.Restore();
            Assert.IsFalse(dmx.disableStrobe, "and puts the author's setting back");
        }

        /// <summary>
        /// Which fixture count the coverage figure divides by.
        ///
        /// Row H9 of TESTING.md.
        /// </summary>
        [Test]
        public void CoverageIsReadAgainstThePathItWasMeasuredOn()
        {
            // One light path: the two counts agree and nothing is qualified.
            var single = new VRSLCounters { fixtures = 12, measuredPathFixtures = 12 };
            Assert.AreEqual(12, single.MeasuredFixtures);
            Assert.IsFalse(single.MixedPaths);

            // Both paths: the coverage average came off one cull pass, so it divides
            // by that path's fixtures. Reading it against the scene total reports
            // less coverage than was measured, and the worst-case branch keyed off
            // that comparison can never fire.
            var both = new VRSLCounters { fixtures = 20, measuredPathFixtures = 8 };
            Assert.AreEqual(8, both.MeasuredFixtures);
            Assert.IsTrue(both.MixedPaths);

            // A run recorded before the field existed deserialises to zero. It has to
            // fall back to the scene count, or every old run reports a denominator of
            // nothing and reads as a scene with no fixtures in it.
            var old = new VRSLCounters { fixtures = 15, measuredPathFixtures = 0 };
            Assert.AreEqual(15, old.MeasuredFixtures);
            Assert.IsFalse(old.MixedPaths, "an absent field is not a mixed scene");
        }

        [Test]
        public void NoManagersAtAllIsToleratedRatherThanThrown()
        {
            // Begin, Apply and Restore are all reachable on a scene that turned out to
            // have nothing in it, and the caller unwinds through Restore in a finally.
            var session = VRSLQualityPreset.Session.Begin(null, null);
            Assert.DoesNotThrow(() => session.Apply(VRSLQualityPreset.Level.High));
            Assert.DoesNotThrow(() => session.Restore());
        }
    }
}
