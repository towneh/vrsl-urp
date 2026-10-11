using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// What ties the AudioLink assembly to the rest of the package: the AudioLink layout
    /// the shaders declare for themselves, the benchmark's marker, and the core reaching
    /// the AudioLink manager and fixtures without naming them.
    ///
    /// Rows Y4, Y5 and Y6 of TESTING.md.
    /// </summary>
    class VRSLAudioLinkIntegrationTests
    {
        const string OwnDeclarations =
            "Packages/town.mr.vrsl-urp/Runtime/Shaders/AudioLink/Shared/VRSL-AudioLink-Data.hlsl";
        const string AudioLinkCginc =
            "Packages/com.llealloo.audiolink/Runtime/Shaders/AudioLink.cginc";

        /// <summary>
        /// Row Y4. The AudioLink fixture shaders declare AudioLink's texture layout
        /// themselves so they compile without the package. Every declaration they make
        /// has to be a line of the AudioLink.cginc installed beside them, or an AudioLink
        /// release that moved a row would light the wrong band with nothing to say so.
        /// </summary>
        [Test]
        public void Y4_TheShadersOwnAudioLinkDeclarationsMatchTheInstalledAudioLink()
        {
            string ours   = Path.GetFullPath(OwnDeclarations);
            string theirs = Path.GetFullPath(AudioLinkCginc);
            Assert.IsTrue(File.Exists(ours),   "the package's own declarations at " + ours);
            Assert.IsTrue(File.Exists(theirs), "AudioLink's AudioLink.cginc at " + theirs);

            var theirLines = File.ReadAllLines(theirs).Select(Normalise).Where(l => l.Length > 0)
                                 .ToHashSet();
            var declarations = File.ReadAllLines(ours).Select(Normalise)
                                   .Where(l => l.Length > 0 && !IsGuard(l)).ToList();

            Assert.That(declarations.Count(l => l.StartsWith("#define ALPASS_")), Is.EqualTo(6),
                        "the six layout positions the shaders read");
            var missing = declarations.Where(l => !theirLines.Contains(l)).ToList();
            Assert.IsEmpty(missing, "declared by the package but not by the installed "
                                  + "AudioLink.cginc, so one of them has drifted:\n"
                                  + string.Join("\n", missing));
        }

        /// <summary>Comments gone, runs of whitespace collapsed to one space, and none
        /// around commas or just inside brackets, so <c>uint2(0, 4)</c> reads as
        /// <c>uint2(0,4)</c>. The space between a macro's name and its body stays: it
        /// is what separates a function-like macro from one whose body starts with a
        /// bracket.</summary>
        static string Normalise(string line)
        {
            int comment = line.IndexOf("//", System.StringComparison.Ordinal);
            if (comment >= 0) line = line.Substring(0, comment);
            line = Regex.Replace(line, @"\s+", " ").Trim();
            line = Regex.Replace(line, @" ?, ?", ",");
            line = Regex.Replace(line, @"([(\[]) ", "$1");
            return Regex.Replace(line, @" ([)\]])", "$1");
        }

        /// <summary>The include guards and branch ends, which say nothing about AudioLink.</summary>
        static bool IsGuard(string line) =>
            line == "#endif" || line == "#else"
            || line.Contains("VRSL_AUDIOLINK_DATA_INCLUDED")
            || line.Contains("AUDIOLINK_CGINC_INCLUDED");

        /// <summary>
        /// Row Y5. The benchmark names the AudioLink decode marker as a string, because the
        /// manager that owns it is in this optional assembly. A rename here that the
        /// benchmark missed would drop the AudioLink decode from every report.
        /// </summary>
        [Test]
        public void Y5_TheBenchmarkListsTheAudioLinkDecodeMarker()
        {
            CollectionAssert.Contains(VRSLBenchmark.PassMarkers,
                                      VRSL_AudioLinkURPLightManager.DecodeMarker);
        }

        GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
        }

        /// <summary>
        /// Row Y6. The control panel reaches the AudioLink manager and AudioLink discoballs
        /// through the light paths the AudioLink assembly registers, and adjusts them as
        /// it did when it named their types: quality, light and beams as a share of what
        /// the scene was authored to, the sample texture, and each discoball's intensity.
        ///
        /// The manager and the discoball are switched off before their GameObject goes
        /// active, so neither claims anything or allocates: the panel only needs their
        /// fields, and it finds components on active GameObjects whether or not they are
        /// enabled.
        /// </summary>
        [Test]
        public void Y6_TheControlPanelAdjustsTheAudioLinkManagerAndDiscoballs()
        {
            _go = new GameObject("vrsl-audiolink-panel");
            _go.SetActive(false);

            var manager = _go.AddComponent<VRSL_AudioLinkURPLightManager>();
            manager.enabled             = false;
            manager.quality             = VRSLQuality.High;
            manager.lightIntensity      = 0.8f;
            manager.volumetricIntensity = 0.6f;

            var discoball = _go.AddComponent<VRStageLighting_AudioLink_RealtimeLight>();
            discoball.enabled         = false;
            discoball.fixtureType     = AudioLinkFixtureType.Discoball;
            discoball.globalIntensity = 0.5f;

            var panel = _go.AddComponent<VRSL_LocalUIControlPanel>();
            panel.enabled          = false;
            panel.lightSlider      = Slider("light");
            panel.volumetricSlider = Slider("beams");
            panel.discoBallSlider  = Slider("discoball");
            var sample = new Texture2D(2, 2);
            panel.videoSampleTargetTexture = sample;

            _go.SetActive(true);
            try
            {
                panel.ResolveManagers();
                panel.CollectMaterials();

                panel.lightSlider.value      = 0.5f;
                panel.volumetricSlider.value = 0.25f;
                panel.discoBallSlider.value  = 0.5f;
                panel._SetLightIntensity();
                panel._SetVolumetricIntensity();
                panel._SetDiscoBallIntensity();
                Assert.AreEqual(0.4f,   manager.lightIntensity,      1e-5f, "light, a share of 0.8");
                Assert.AreEqual(0.15f,  manager.volumetricIntensity, 1e-5f, "beams, a share of 0.6");
                Assert.AreEqual(0.25f,  discoball.globalIntensity,   1e-5f, "the discoball, a share of 0.5");

                // Resolving again, as each inspector button does, keeps the manager it holds
                // and what that manager was authored to rather than the scaled value.
                panel.ResolveManagers();
                panel._SetLightIntensity();
                Assert.AreEqual(0.4f, manager.lightIntensity, 1e-5f,
                                "still a share of the authored 0.8 after resolving again");

                panel.volumetricQuality = VolumetricQualityModes.Off;
                panel._UpdateAllQualityModes();
                Assert.AreEqual(VRSLQuality.Off, manager.quality, "the quality buttons");

                panel._ForceUpdateVideoSampleTexture();
                Assert.AreSame(sample, manager.samplingTexture, "the sample texture");
            }
            finally
            {
                Object.DestroyImmediate(sample);
            }
        }

        Slider Slider(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_go.transform);
            var slider = go.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value    = 1f;
            return slider;
        }
    }
}
