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
            manager.volumetricShader = _shader;
            manager.quality          = VRSLQuality.High;

            var session = VRSLQualityPreset.Session.Begin(null, manager);

            session.Apply(VRSLQuality.Off);
            Assert.AreEqual(VRSLQuality.Off, manager.quality);
            Assert.IsFalse(manager.VolumetricsEnabled,
                "Off has to stop the pass being recorded, not merely draw nothing");
            Assert.AreEqual(Vector4.zero, manager.ContactShadowParams,
                "and stop the trace, which the shader reads a zero step count as");

            session.Apply(VRSLQuality.Standard);
            Assert.IsTrue(manager.VolumetricsEnabled, "the shader was never taken away");
            Assert.AreEqual(24, (int)manager.VolumetricStepParams.x);

            session.Apply(VRSLQuality.High);
            Assert.AreEqual(40, (int)manager.VolumetricStepParams.x);

            session.Restore();
            Assert.AreEqual(_shader, manager.volumetricShader);
            Assert.AreEqual(VRSLQuality.High, manager.quality,
                            "the author's own level goes back");
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

            session.Apply(VRSLQuality.Off);
            Assert.IsFalse(dmx.VolumetricsEnabled,       "the DMX manager is held at the level");
            Assert.IsFalse(audioLink.VolumetricsEnabled, "and so is the AudioLink one");

            session.Apply(VRSLQuality.High);
            Assert.AreEqual(40, (int)dmx.VolumetricStepParams.x);
            Assert.AreEqual(40, (int)audioLink.VolumetricStepParams.x);

            session.Restore();
            Assert.AreEqual(VRSLQuality.Standard, dmx.quality, "back to what a fresh manager is");
            Assert.AreEqual(VRSLQuality.Standard, audioLink.quality);
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

            // And that fallback is why a zero must never be written by a scene that has
            // a path worth measuring: read back, it is indistinguishable from the run
            // above. The guard belongs where the path is chosen, not here — see
            // AnEmptyManagerIsNotTheMeasuredPath.
        }

        [Test]
        public void AnEmptyManagerIsNotTheMeasuredPath()
        {
            // One path present: it is the measured one whether or not it holds anything,
            // because there is no other to name.
            Assert.IsTrue(VRSLBenchmark.MeasureDmxPath(true, 0, false), "DMX alone, empty");
            Assert.IsTrue(VRSLBenchmark.MeasureDmxPath(true, 9, false), "DMX alone");
            Assert.IsFalse(VRSLBenchmark.MeasureDmxPath(false, 0, true), "AudioLink alone");

            // Both present and both populated: DMX wins, so one cull pass is described
            // rather than an average of two.
            Assert.IsTrue(VRSLBenchmark.MeasureDmxPath(true, 9, true), "both, DMX populated");

            // Both present and the DMX one empty. This is the row: selecting it writes a
            // measured-path count of zero, and zero is exactly what a run too old to carry
            // the field deserialises to — so the scene reads as single-path and the
            // AudioLink coverage gets divided by the AudioLink count with nothing saying
            // it was measured over a path holding no fixtures at all.
            Assert.IsFalse(VRSLBenchmark.MeasureDmxPath(true, 0, true),
                           "an empty DMX manager beside a populated AudioLink one");
        }

        /// <summary>
        /// The constants table is the contract, and Standard is what the package
        /// shipped before the numeric fields were removed.
        ///
        /// Row Q4 of TESTING.md. Written down here because the fields it reproduces no
        /// longer exist to be compared against: once they were deleted, "Standard costs
        /// what the defaults cost" stopped being checkable from the code and became a
        /// claim somebody has to pin.
        /// </summary>
        [Test]
        public void StandardReproducesTheDefaultsItReplaced()
        {
            var standard = VRSLQualityLevel.For(VRSLQuality.Standard);
            Assert.IsTrue(standard.Volumetrics);
            Assert.AreEqual(24,    standard.VolumetricMaxSteps,     "volumetricStepCount");
            Assert.IsTrue(standard.VolumetricNoise,                 "volumetricUseNoise");
            Assert.IsTrue(standard.ContactShadows);
            Assert.AreEqual(8,     standard.ContactShadowSteps,     "contactShadowSteps");
            Assert.AreEqual(1.5f,  standard.ContactShadowDistance,  "contactShadowDistance");
            Assert.AreEqual(0.5f,  standard.ContactShadowThickness, "contactShadowThickness");
            Assert.AreEqual(0.3f,  VRSLQualityLevel.NoiseScale,     "volumetricNoiseScale");
            Assert.AreEqual(0.1f,  VRSLQualityLevel.NoiseScrollSpeed, "volumetricNoiseScrollSpeed");
            Assert.AreEqual(0.7f,  VRSLQualityLevel.NoiseStrength,  "volumetricNoiseStrength");

            var off = VRSLQualityLevel.For(VRSLQuality.Off);
            Assert.IsFalse(off.Volumetrics);
            Assert.IsFalse(off.ContactShadows);

            var high = VRSLQualityLevel.For(VRSLQuality.High);
            Assert.Greater(high.VolumetricMaxSteps, standard.VolumetricMaxSteps,
                           "High is Standard's look spent more finely, or it is not a level");
            Assert.Greater(high.ContactShadowSteps, standard.ContactShadowSteps);

            // A value outside the enum is what a scene deserialises when somebody edits
            // the asset by hand or a future level is removed. It has to land somewhere
            // that renders rather than somewhere that draws nothing.
            var strange = VRSLQualityLevel.For((VRSLQuality)97);
            Assert.IsTrue(strange.Volumetrics, "an unrecognised level falls back to Standard");
            Assert.AreEqual(standard.VolumetricMaxSteps, strange.VolumetricMaxSteps);
        }

        /// <summary>
        /// Both managers answer from the same table.
        ///
        /// Row Q5. A scene carrying both paths would otherwise be able to march at two
        /// different budgets depending on which manager owned the pass, and the beams
        /// would not match each other.
        /// </summary>
        [Test]
        public void BothManagersAgreeOnWhatALevelCosts()
        {
            var dmx       = _go.AddComponent<VRSL_URPLightManager>();
            var audioLink = _go.AddComponent<VRSL_AudioLinkURPLightManager>();
            dmx.volumetricShader       = _shader;
            audioLink.volumetricShader = _shader;
            dmx.contactShadowStrength       = 1f;
            audioLink.contactShadowStrength = 1f;

            foreach (var level in VRSLQualityPreset.All)
            {
                dmx.quality       = level;
                audioLink.quality = level;
                Assert.AreEqual(dmx.VolumetricStepParams,    audioLink.VolumetricStepParams,    $"{level} steps");
                Assert.AreEqual(dmx.VolumetricDensityParams, audioLink.VolumetricDensityParams, $"{level} density");
                Assert.AreEqual(dmx.ContactShadowParams,     audioLink.ContactShadowParams,     $"{level} contact");
                Assert.AreEqual(dmx.VolumetricsEnabled,      audioLink.VolumetricsEnabled,      $"{level} enabled");
                Assert.AreEqual(dmx.VolumetricUseNoise,      audioLink.VolumetricUseNoise,      $"{level} noise");
            }
        }

        /// <summary>
        /// A strength of zero stops the trace rather than tracing and multiplying by it.
        ///
        /// Row Q3. The trace is the most expensive term in the lighting loop, so
        /// running it to scale the result to nothing is the whole cost for none of the
        /// benefit — and it reads as working, because the picture is right.
        /// </summary>
        [Test]
        public void ZeroContactShadowStrengthStopsTheTrace()
        {
            var dmx = _go.AddComponent<VRSL_URPLightManager>();
            dmx.quality = VRSLQuality.High;

            dmx.contactShadowStrength = 0f;
            Assert.AreEqual(Vector4.zero, dmx.ContactShadowParams,
                            "zero strength has to zero the step count, which is the skip");

            dmx.contactShadowStrength = 0.5f;
            Assert.AreEqual(16, (int)dmx.ContactShadowParams.z, "and a real strength traces");

            dmx.quality = VRSLQuality.Off;
            Assert.AreEqual(Vector4.zero, dmx.ContactShadowParams,
                            "as does a level with no contact shadows, whatever the strength");
        }

        [Test]
        public void NoManagersAtAllIsToleratedRatherThanThrown()
        {
            // Begin, Apply and Restore are all reachable on a scene that turned out to
            // have nothing in it, and the caller unwinds through Restore in a finally.
            var session = VRSLQualityPreset.Session.Begin(null, null);
            Assert.DoesNotThrow(() => session.Apply(VRSLQuality.High));
            Assert.DoesNotThrow(() => session.Restore());
        }
    }
}
