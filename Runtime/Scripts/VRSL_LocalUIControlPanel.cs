using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
using System.IO;
#endif

namespace VRSL.URP
{
    /// <summary>The three beam settings the panel offers, in the order the buttons sit.
    /// Stored by value, so a scene saved with the earlier High / Medium / Low names
    /// keeps the same choice.</summary>
    public enum VolumetricQualityModes
    {
        High,
        Standard,
        Off
    }

    public enum DefaultQualityModes
    {
        High,
        Low
    }

    /// <summary>
    /// An in-world panel the local user can adjust the lighting from: how bright the
    /// fixtures, their light and their beams are, how much the beams cost, and whether
    /// strobes run. It drives the scene's URP light managers for the light and the beams,
    /// and the fixture materials for the parts that are still shader-driven (the body
    /// glow, the lens flares, the discoball and the lasers). Nothing it sets leaves the
    /// local client.
    /// </summary>
    public class VRSL_LocalUIControlPanel : MonoBehaviour
    {
        [Header("Light managers")]
        [Tooltip("The DMX light manager this panel adjusts. Leave empty to use the one in "
               + "the scene.")]
        public VRSL_URPLightManager dmxManager;
        [Tooltip("The AudioLink light manager this panel adjusts. Leave empty to use the one "
               + "in the scene.")]
        public VRSL_AudioLinkURPLightManager audioLinkManager;

        [Header("Quality")]
        [Tooltip("How much the beams cost. High marches them more finely, Standard suits "
               + "most worlds, Off keeps the light on surfaces and removes the beams. Set on "
               + "both light managers when a button is pressed; on start the panel shows "
               + "whatever the scene's manager is set to.")]
        public VolumetricQualityModes volumetricQuality = VolumetricQualityModes.Standard;
        [Tooltip("Stop the user changing the beam quality from the panel.")]
        public bool lockVolumetricQualityMode;
        [Space(5.0f)]
        [Tooltip("High blends the discoball's spots; Low dithers them, which is cheaper "
               + "under MSAA.")]
        public DefaultQualityModes discoballQuality;
        public bool lockDiscoballQualityMode;
        [Space(5.0f)]
        [Tooltip("High blends the lens flares; Low dithers them, which is cheaper under MSAA.")]
        public DefaultQualityModes lensFlareQuality;
        public bool lockLensFlareQualityMode;

        [Header("AudioLink colour sampling")]
        [Tooltip("A texture for AudioLink fixtures set to sample their colour from a texture, "
               + "a video feed for instance. Goes to the AudioLink light manager and to any "
               + "laser material that samples one. Leave empty to change nothing.")]
        public Texture videoSampleTargetTexture;

        [Header("Post Processing Animators")]
        public Animator bloomAnimator;

        [Space(5)]
        [Header("UI Sliders")]
        public Slider masterSlider;
        public Slider fixtureSlider;
        public Slider volumetricSlider;
        public Slider lightSlider;
        public Slider discoBallSlider;
        public Slider laserSlider;
        public Slider bloomSlider;
        public Text masterSliderText, fixtureSliderText, volumetricSliderText, lightSliderText, discoBallSliderText, laserSliderText, bloomSliderText;
        public float fixtureIntensityMax = 1.0f, volumetricIntensityMax = 1.0f, lightIntensityMax = 1.0f, discoballIntensityMax = 1.0f, laserIntensityMax = 1.0f;

        public Button volumetricHighButton, volumetricMedButton, volumetricLowButton;
        public Text volumetricHighText, volumetricMedText, volumetricLowText;
        public Button discoballHighButton, discoballLowButton;
        public Text discoballHighText, discoballLowText;
        public Button lensFlareHighButton, lensFlareLowButton;
        public Text lensFlareHighText, lensFlareLowText;

        public Button globalStrobeToggleButton;
        public Text globalStrobeLabel;
        ColorBlock defaultColorBlock;
        ColorBlock cbOn;

        [SerializeField]
        private bool _globalDisableStrobe = false;

        /// <summary>Hold every strobing fixture fully on. Reaches the DMX manager, which
        /// gates the cast light and the beams, and the strobe decode material the fixture
        /// bodies sample.</summary>
        public bool GlobalDisableStrobe
        {
            set
            {
                _globalDisableStrobe = value;
                SetStrobeStatus();
            }
            get => _globalDisableStrobe;
        }

        // Materials found in the scene at start, grouped by the part of a fixture they
        // draw. The managers own the light and the beams; these are what is still set on
        // the shader side.
        readonly List<Material> _fixtureMaterials   = new();
        readonly List<Material> _lensFlareMaterials = new();
        readonly List<Material> _discoBallMaterials = new();
        readonly List<Material> _laserMaterials     = new();

        // What the managers were authored to, so the sliders scale the scene's own values
        // rather than replacing them.
        float _dmxAuthoredLight = 1f, _dmxAuthoredBeams = 1f;
        float _alAuthoredLight  = 1f, _alAuthoredBeams  = 1f;

        static readonly int s_UniversalIntensity = Shader.PropertyToID("_UniversalIntensity");
        static readonly int s_SamplingTexture    = Shader.PropertyToID("_SamplingTexture");
        static readonly int s_DisableStrobe      = Shader.PropertyToID("_DisableStrobe");

        public void _ToggleGlobalStrobe()
        {
            GlobalDisableStrobe = !GlobalDisableStrobe;
        }

        void Start()
        {
            if (volumetricHighButton)
            {
                defaultColorBlock = volumetricHighButton.colors;
                cbOn = defaultColorBlock;
                cbOn.normalColor = new Color(cbOn.normalColor.r + 0.35f, cbOn.normalColor.r + 0.35f, cbOn.normalColor.g + 0.35f, 1.0f);
            }
            if (bloomAnimator == null)
            {
                GameObject anim = GameObject.Find("PostProcessingExample-Bloom");
                if (anim != null)
                {
                    bloomAnimator = anim.GetComponent<Animator>();
                }
            }
            ResolveManagers();
            CaptureAuthoredValues();
            CollectMaterials();
            AdoptManagerQuality();
            _SetFinalIntensity();
            _SetBloomIntensity();
            _ForceUpdateVideoSampleTexture();
            RefreshVolumetricButtons();
            _SetDiscoBallQualityMode();
            _SetLensFlareQualtiyMode();
            _CheckButtonLockStatus();
            SetStrobeStatus();
        }

        /// <summary>Show the beam quality the scene was authored to, so adding the panel
        /// changes nothing until a button is pressed. The DMX manager wins where the two
        /// differ.</summary>
        void AdoptManagerQuality()
        {
            var source = dmxManager != null ? dmxManager.quality
                       : audioLinkManager != null ? audioLinkManager.quality
                       : (VRSLQuality?)null;
            if (source == null) return;
            switch (source.Value)
            {
                case VRSLQuality.High: volumetricQuality = VolumetricQualityModes.High; break;
                case VRSLQuality.Off:  volumetricQuality = VolumetricQualityModes.Off;  break;
                default:               volumetricQuality = VolumetricQualityModes.Standard; break;
            }
        }

        /// <summary>Find the managers when none is assigned. Safe to call again; it only
        /// fills empty references.</summary>
        public void ResolveManagers()
        {
            if (dmxManager == null)
                dmxManager = VRSL_URPLightManager.Instance != null
                    ? VRSL_URPLightManager.Instance
                    : FindAnyObjectByType<VRSL_URPLightManager>();
            if (audioLinkManager == null)
                audioLinkManager = VRSL_AudioLinkURPLightManager.Instance != null
                    ? VRSL_AudioLinkURPLightManager.Instance
                    : FindAnyObjectByType<VRSL_AudioLinkURPLightManager>();
        }

        /// <summary>Remember what the managers were authored to, once, before any slider
        /// has scaled them.</summary>
        void CaptureAuthoredValues()
        {
            if (dmxManager != null)
            {
                _dmxAuthoredLight = dmxManager.lightIntensity;
                _dmxAuthoredBeams = dmxManager.volumetricIntensity;
            }
            if (audioLinkManager != null)
            {
                _alAuthoredLight = audioLinkManager.lightIntensity;
                _alAuthoredBeams = audioLinkManager.volumetricIntensity;
            }
        }

        /// <summary>Walk the scene's renderers once and keep every VRSL fixture material,
        /// sorted by what it draws. Materials are shared assets, so a change made here
        /// reaches every fixture using that material.</summary>
        public void CollectMaterials()
        {
            _fixtureMaterials.Clear();
            _lensFlareMaterials.Clear();
            _discoBallMaterials.Clear();
            _laserMaterials.Clear();

            var seen = new HashSet<Material>();
            foreach (var renderer in FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || mat.shader == null || !seen.Add(mat)) continue;
                    string shader = mat.shader.name;
                    if (!shader.StartsWith("VRSL-URP/")) continue;

                    if (shader.Contains("Lens Flare"))        _lensFlareMaterials.Add(mat);
                    else if (shader.Contains("Discoball"))    _discoBallMaterials.Add(mat);
                    else if (shader.Contains("Basic Laser"))  _laserMaterials.Add(mat);
                    else if (mat.HasProperty(s_UniversalIntensity)) _fixtureMaterials.Add(mat);
                }
            }
        }

        void _CheckButtonLockStatus()
        {
            Color disableColor = new Color(0.25f, 0.25f, 0.25f, 1.0f);
            Color disableButEnabledColor = new Color(0.4f, 0.4f, 0.4f, 1.0f);
            Color disabledTextColor = new Color(1.0f, 1.0f, 1.0f, 0.045f);
            if (lockVolumetricQualityMode)
            {
                if (volumetricHighButton)
                {
                    volumetricHighButton.image.color = volumetricQuality == VolumetricQualityModes.High ? disableButEnabledColor : disableColor;
                    volumetricHighButton.interactable = false;
                }
                if (volumetricMedButton)
                {
                    volumetricMedButton.image.color = volumetricQuality == VolumetricQualityModes.Standard ? disableButEnabledColor : disableColor;
                    volumetricMedButton.interactable = false;
                }
                if (volumetricLowButton)
                {
                    volumetricLowButton.image.color = volumetricQuality == VolumetricQualityModes.Off ? disableButEnabledColor : disableColor;
                    volumetricLowButton.interactable = false;
                }
                if (volumetricHighText) { volumetricHighText.color = disabledTextColor; }
                if (volumetricMedText) { volumetricMedText.color = disabledTextColor; }
                if (volumetricLowText) { volumetricLowText.color = disabledTextColor; }
            }
            if (lockLensFlareQualityMode)
            {
                if (lensFlareHighButton)
                {
                    lensFlareHighButton.image.color = lensFlareQuality == DefaultQualityModes.High ? disableButEnabledColor : disableColor;
                    lensFlareHighButton.interactable = false;
                }
                if (lensFlareLowButton)
                {
                    lensFlareLowButton.image.color = lensFlareQuality == DefaultQualityModes.Low ? disableButEnabledColor : disableColor;
                    lensFlareLowButton.interactable = false;
                }
                if (lensFlareHighText) { lensFlareHighText.color = disabledTextColor; }
                if (lensFlareLowText) { lensFlareLowText.color = disabledTextColor; }
            }
            if (lockDiscoballQualityMode)
            {
                if (discoballHighButton)
                {
                    discoballHighButton.image.color = discoballQuality == DefaultQualityModes.High ? disableButEnabledColor : disableColor;
                    discoballHighButton.interactable = false;
                }
                if (discoballLowButton)
                {
                    discoballLowButton.image.color = discoballQuality == DefaultQualityModes.Low ? disableButEnabledColor : disableColor;
                    discoballLowButton.interactable = false;
                }
                if (discoballHighText) { discoballHighText.color = disabledTextColor; }
                if (discoballLowText) { discoballLowText.color = disabledTextColor; }
            }
        }

        public void _SetVolumetricHigh()
        {
            if (lockVolumetricQualityMode) { return; }
            volumetricQuality = VolumetricQualityModes.High;
            _SetVolumetricQualityMode();
        }
        public void _SetVolumetricStandard()
        {
            if (lockVolumetricQualityMode) { return; }
            volumetricQuality = VolumetricQualityModes.Standard;
            _SetVolumetricQualityMode();
        }
        public void _SetVolumetricOff()
        {
            if (lockVolumetricQualityMode) { return; }
            volumetricQuality = VolumetricQualityModes.Off;
            _SetVolumetricQualityMode();
        }
        public void _SetDiscoballHigh()
        {
            if (lockDiscoballQualityMode) { return; }
            discoballQuality = DefaultQualityModes.High;
            _SetDiscoBallQualityMode();
        }
        public void _SetDiscoballLow()
        {
            if (lockDiscoballQualityMode) { return; }
            discoballQuality = DefaultQualityModes.Low;
            _SetDiscoBallQualityMode();
        }
        public void _SetLensFlareHigh()
        {
            if (lockLensFlareQualityMode) { return; }
            lensFlareQuality = DefaultQualityModes.High;
            _SetLensFlareQualtiyMode();
        }
        public void _SetLensFlareLow()
        {
            if (lockLensFlareQualityMode) { return; }
            lensFlareQuality = DefaultQualityModes.Low;
            _SetLensFlareQualtiyMode();
        }
        public void _UpdateAllQualityModes()
        {
            _SetDiscoBallQualityMode();
            _SetVolumetricQualityMode();
            _SetLensFlareQualtiyMode();
        }
        public void _SetVolumetricQualityMode()
        {
            RefreshVolumetricButtons();
            SetVolumetricQuality();
        }

        void RefreshVolumetricButtons()
        {
            switch (volumetricQuality)
            {
                case VolumetricQualityModes.High:
                    if (volumetricHighButton) { volumetricHighButton.colors = cbOn; }
                    if (volumetricMedButton) { volumetricMedButton.colors = defaultColorBlock; }
                    if (volumetricLowButton) { volumetricLowButton.colors = defaultColorBlock; }
                    break;
                case VolumetricQualityModes.Standard:
                    if (volumetricHighButton) { volumetricHighButton.colors = defaultColorBlock; }
                    if (volumetricMedButton) { volumetricMedButton.colors = cbOn; }
                    if (volumetricLowButton) { volumetricLowButton.colors = defaultColorBlock; }
                    break;
                case VolumetricQualityModes.Off:
                    if (volumetricHighButton) { volumetricHighButton.colors = defaultColorBlock; }
                    if (volumetricMedButton) { volumetricMedButton.colors = defaultColorBlock; }
                    if (volumetricLowButton) { volumetricLowButton.colors = cbOn; }
                    break;
                default:
                    break;
            }
        }
        public void _SetDiscoBallQualityMode()
        {
            switch (discoballQuality)
            {
                case DefaultQualityModes.High:
                    if (discoballHighButton) { discoballHighButton.colors = cbOn; }
                    if (discoballLowButton) { discoballLowButton.colors = defaultColorBlock; }
                    break;
                case DefaultQualityModes.Low:
                    if (discoballHighButton) { discoballHighButton.colors = defaultColorBlock; }
                    if (discoballLowButton) { discoballLowButton.colors = cbOn; }
                    break;
                default:
                    break;
            }
            SetDiscoballQuality();
        }
        public void _SetLensFlareQualtiyMode()
        {
            switch (lensFlareQuality)
            {
                case DefaultQualityModes.High:
                    if (lensFlareHighButton) { lensFlareHighButton.colors = cbOn; }
                    if (lensFlareLowButton) { lensFlareLowButton.colors = defaultColorBlock; }
                    break;
                case DefaultQualityModes.Low:
                    if (lensFlareHighButton) { lensFlareHighButton.colors = defaultColorBlock; }
                    if (lensFlareLowButton) { lensFlareLowButton.colors = cbOn; }
                    break;
                default:
                    break;
            }
            SetLensFlareQuality();
        }

        void SetGlobalStrobeUI()
        {
            if (globalStrobeToggleButton)
            {
                globalStrobeToggleButton.colors = GlobalDisableStrobe ? cbOn : defaultColorBlock;
                globalStrobeToggleButton.gameObject.SetActive(dmxManager != null);
            }
        }

        void SetStrobeStatus()
        {
            if (dmxManager != null)
            {
                dmxManager.disableStrobe = _globalDisableStrobe;
                if (dmxManager.dmxStrobeTexture is CustomRenderTexture strobe
                    && strobe.material != null && strobe.material.HasProperty(s_DisableStrobe))
                {
                    strobe.material.SetFloat(s_DisableStrobe, _globalDisableStrobe ? 1f : 0f);
                }
            }
            SetGlobalStrobeUI();
        }

        public void _ForceUpdateVideoSampleTexture()
        {
            if (videoSampleTargetTexture == null)
            {
                return;
            }
            if (audioLinkManager != null)
            {
                audioLinkManager.samplingTexture = videoSampleTargetTexture;
            }
            foreach (Material m in _laserMaterials)
            {
                if (m.HasProperty(s_SamplingTexture))
                {
                    m.SetTexture(s_SamplingTexture, videoSampleTargetTexture);
                }
            }
        }

        public void _SetFinalIntensity()
        {
            if (masterSlider != null)
            {
                fixtureIntensityMax = masterSlider.value;
                volumetricIntensityMax = masterSlider.value;
                lightIntensityMax = masterSlider.value;
                discoballIntensityMax = masterSlider.value;
                laserIntensityMax = masterSlider.value;
                if (masterSliderText != null)
                    masterSliderText.text = Mathf.Round(masterSlider.value * 100.0f).ToString();
            }
            _SetFixtureIntensity();
            _SetVolumetricIntensity();
            _SetLightIntensity();
            _SetDiscoBallIntensity();
            _SetLaserIntensity();
        }

        /// <summary>The fixture bodies' own glow.</summary>
        public void _SetFixtureIntensity()
        {
            if (fixtureSlider == null) return;
            float value = Mathf.Lerp(0.0f, fixtureIntensityMax, fixtureSlider.value);
            foreach (Material mat in _fixtureMaterials)
            {
                mat.SetFloat(s_UniversalIntensity, value);
            }
            foreach (Material mat in _lensFlareMaterials)
            {
                mat.SetFloat(s_UniversalIntensity, value);
            }
            if (fixtureSliderText != null)
                fixtureSliderText.text = Mathf.Round(fixtureSlider.value * 100.0f).ToString();
        }

        /// <summary>The beams, on both managers, as a share of what the scene was
        /// authored to.</summary>
        public void _SetVolumetricIntensity()
        {
            if (volumetricSlider == null) return;
            float scale = Mathf.Lerp(0.0f, volumetricIntensityMax, volumetricSlider.value);
            if (dmxManager != null) dmxManager.volumetricIntensity = _dmxAuthoredBeams * scale;
            if (audioLinkManager != null) audioLinkManager.volumetricIntensity = _alAuthoredBeams * scale;
            if (volumetricSliderText != null)
                volumetricSliderText.text = Mathf.Round(volumetricSlider.value * 100.0f).ToString();
        }

        /// <summary>The light the fixtures cast, on both managers, as a share of what the
        /// scene was authored to. The beams follow it, since they are the same light.</summary>
        public void _SetLightIntensity()
        {
            if (lightSlider == null) return;
            float scale = Mathf.Lerp(0.0f, lightIntensityMax, lightSlider.value);
            if (dmxManager != null)
            {
                dmxManager.lightIntensity = _dmxAuthoredLight * scale;
                dmxManager.MarkConfigDirty();
            }
            if (audioLinkManager != null) audioLinkManager.lightIntensity = _alAuthoredLight * scale;
            if (lightSliderText != null)
                lightSliderText.text = Mathf.Round(lightSlider.value * 100.0f).ToString();
        }

        public void _SetDiscoBallIntensity()
        {
            if (discoBallSlider == null) return;
            float value = Mathf.Lerp(0.0f, discoballIntensityMax, discoBallSlider.value);
            foreach (Material mat in _discoBallMaterials)
            {
                mat.SetFloat(s_UniversalIntensity, value);
            }
            if (discoBallSliderText != null)
                discoBallSliderText.text = Mathf.Round(discoBallSlider.value * 100.0f).ToString();
        }

        public void _SetLaserIntensity()
        {
            if (laserSlider == null) return;
            float value = Mathf.Lerp(0.0f, laserIntensityMax, laserSlider.value);
            foreach (Material mat in _laserMaterials)
            {
                mat.SetFloat(s_UniversalIntensity, value);
            }
            if (laserSliderText != null)
                laserSliderText.text = Mathf.Round(laserSlider.value * 100.0f).ToString();
        }

        public void _SetBloomIntensity()
        {
            if (bloomSlider == null) return;
            if (bloomAnimator != null)
            {
                bloomAnimator.SetFloat("BloomIntensity", bloomSlider.value);
                if (bloomSliderText != null)
                    bloomSliderText.text = Mathf.Round(bloomSlider.value * 100.0f).ToString();
            }
            else
            {
                bloomSlider.gameObject.SetActive(false);
            }
        }

        static VRSLQuality ToManagerQuality(VolumetricQualityModes mode)
        {
            switch (mode)
            {
                case VolumetricQualityModes.High: return VRSLQuality.High;
                case VolumetricQualityModes.Off:  return VRSLQuality.Off;
                default:                          return VRSLQuality.Standard;
            }
        }

        void SetVolumetricQuality()
        {
            var quality = ToManagerQuality(volumetricQuality);
            if (dmxManager != null) dmxManager.quality = quality;
            if (audioLinkManager != null) audioLinkManager.quality = quality;
        }

        // The discoball and the lens flares are shader fixtures with a blended mode and a
        // dithered, alpha-to-coverage mode. Both modes are set through the material.
        static void SetBlendedMode(Material target, bool blended)
        {
            if (blended)
            {
                target.SetOverrideTag("RenderType", "Transparent");
                target.DisableKeyword("_ALPHATEST_ON");
                target.SetInt("_BlendDst", 1);
                target.SetInt("_ZWrite", 0);
                target.SetInt("_AlphaToCoverage", 0);
                target.SetInt("_RenderMode", 1);
                target.renderQueue = 3001;
            }
            else
            {
                target.SetOverrideTag("RenderType", "Opaque");
                target.EnableKeyword("_ALPHATEST_ON");
                target.SetInt("_BlendDst", 0);
                target.SetInt("_ZWrite", 1);
                target.SetInt("_AlphaToCoverage", 1);
                target.SetInt("_RenderMode", 2);
                target.renderQueue = 2451;
            }
        }

        void SetDiscoballQuality()
        {
            foreach (Material target in _discoBallMaterials)
                SetBlendedMode(target, discoballQuality == DefaultQualityModes.High);
        }

        void SetLensFlareQuality()
        {
            foreach (Material target in _lensFlareMaterials)
                SetBlendedMode(target, lensFlareQuality == DefaultQualityModes.High);
        }
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(VRSL_LocalUIControlPanel))]
    public class VRSL_LocalUIControlPanel_Editor : Editor
    {
        public static Texture logo;

        static string GetVersion()
        {
            // Version ships in Runtime/VERSION.txt; fall back gracefully when it can't be
            // read (e.g. a package-cache install where the dataPath-relative path misses)
            // so a missing file never aborts the inspector before the fields draw.
            string versionNum = "0.1.0";
            try
            {
                string path = Application.dataPath.Replace("Assets", "")
                            + "Packages/town.mr.vrsl-urp/Runtime/VERSION.txt";
                if (File.Exists(path))
                    versionNum = File.ReadAllText(path).Trim();
            }
            catch { /* keep the fallback version */ }
            return "VR Stage Lighting ver:" + " <b><color=#b33cff>" + versionNum + "</color></b>";
        }

        public void OnEnable()
        {
            logo = Resources.Load("VRStageLighting-Logo") as Texture;
        }

        public static void DrawLogo()
        {
            Vector2 contentOffset = new Vector2(0f, -2f);
            GUIStyle style = new GUIStyle(EditorStyles.label);
            style.fixedHeight = 150;
            style.contentOffset = contentOffset;
            style.alignment = TextAnchor.MiddleCenter;
            var rect = GUILayoutUtility.GetRect(300f, 140f, style);
            GUI.Box(rect, logo, style);
        }

        private static Rect DrawShurikenCenteredTitle(string title, Vector2 contentOffset, int HeaderHeight)
        {
            var style = new GUIStyle("ShurikenModuleTitle");
            style.font = new GUIStyle(EditorStyles.boldLabel).font;
            style.border = new RectOffset(15, 7, 4, 4);
            style.fontSize = 14;
            style.fixedHeight = HeaderHeight;
            style.contentOffset = contentOffset;
            style.alignment = TextAnchor.MiddleCenter;
            var rect = GUILayoutUtility.GetRect(16f, HeaderHeight, style);
            GUI.Box(rect, title, style);
            return rect;
        }

        public static void ShurikenHeaderCentered(string title)
        {
            DrawShurikenCenteredTitle(title, new Vector2(0f, -2f), 22);
        }

        static string ManagerStatus(VRSL_LocalUIControlPanel panel)
        {
            var dmx = panel.dmxManager != null ? panel.dmxManager : Object.FindAnyObjectByType<VRSL_URPLightManager>();
            var al  = panel.audioLinkManager != null ? panel.audioLinkManager : Object.FindAnyObjectByType<VRSL_AudioLinkURPLightManager>();
            if (dmx == null && al == null)
                return "No URP light manager in this scene. The beam quality buttons and the "
                     + "light and beam sliders will do nothing until one is added.";
            var parts = new List<string>();
            if (dmx != null) parts.Add("the DMX manager '" + dmx.name + "'");
            if (al != null)  parts.Add("the AudioLink manager '" + al.name + "'");
            return "Adjusts " + string.Join(" and ", parts) + " for the local user.";
        }

        public override void OnInspectorGUI()
        {
            EditorGUI.BeginChangeCheck();
            serializedObject.Update();
            DrawLogo();
            ShurikenHeaderCentered(GetVersion());
            EditorGUILayout.Space();
            VRSL_LocalUIControlPanel controlPanel = (VRSL_LocalUIControlPanel)target;
            EditorGUILayout.HelpBox(ManagerStatus(controlPanel), MessageType.Info);
            EditorGUILayout.Space();
            if (GUILayout.Button(new GUIContent("Send Sample Texture To AudioLink Fixtures",
                    "Hands the sample texture above to the AudioLink light manager and to any laser material that samples one.")))
            {
                controlPanel.ResolveManagers();
                controlPanel.CollectMaterials();
                controlPanel._ForceUpdateVideoSampleTexture();
                if (controlPanel.audioLinkManager != null) EditorUtility.SetDirty(controlPanel.audioLinkManager);
            }
            EditorGUILayout.Space();
            if (GUILayout.Button(new GUIContent("Apply Quality Settings Now",
                    "Pushes the beam quality to the light managers and the discoball and lens flare modes to their materials.")))
            {
                controlPanel.ResolveManagers();
                controlPanel.CollectMaterials();
                controlPanel._UpdateAllQualityModes();
                if (controlPanel.dmxManager != null) EditorUtility.SetDirty(controlPanel.dmxManager);
                if (controlPanel.audioLinkManager != null) EditorUtility.SetDirty(controlPanel.audioLinkManager);
            }
            EditorGUILayout.Space();
            base.OnInspectorGUI();
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                Repaint();
            }
        }
    }
#endif
}
