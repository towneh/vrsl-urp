using NUnit.Framework;
using UnityEngine;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The AudioLink halves of row H8, and row Q5: the quality preset reaches the
    /// AudioLink manager, and both managers answer a level from the same table.
    ///
    /// The managers are built on an inactive GameObject for the reason the DMX halves
    /// give in <c>VRSLQualityPresetTests</c>: `Awake` and `OnEnable` never fire, so a
    /// row cannot start a light manager as a side effect of asking what a preset writes.
    /// </summary>
    class VRSLAudioLinkQualityPresetTests
    {
        GameObject _go;
        Shader     _shader;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("vrsl-quality-preset-target");
            _go.SetActive(false);

            _shader = Shader.Find("Hidden/VRSL-URP/VolumetricLighting");
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
            var dmx       = _go.AddComponent<VRSL_DMXURPLightManager>();
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
            var dmx       = _go.AddComponent<VRSL_DMXURPLightManager>();
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
        /// The AudioLink manager's wiring table names fields it actually has: a rename
        /// that compiles cleanly would otherwise stop resolution silently.
        /// </summary>
        [Test]
        public void EveryAudioLinkWiringFieldNameExistsOnItsManager()
        {
            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;
            foreach (var field in VRSL_AudioLinkURPLightManager.WiringFields)
                Assert.IsNotNull(typeof(VRSL_AudioLinkURPLightManager).GetField(field.Field, Flags),
                                 $"the AudioLink table names '{field.Field}', which the manager does not have");

            var manager = _go.AddComponent<VRSL_AudioLinkURPLightManager>();
            Assert.AreSame(VRSL_AudioLinkURPLightManager.WiringFields, VRSLWiring.FieldsFor(manager),
                           "the table the AudioLink assembly registers is the one wiring uses");
        }
    }
}
