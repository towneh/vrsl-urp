#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace VRSL.URP
{
    [CustomEditor(typeof(VRStageLighting_DMX_RealtimeLight))]
    [CanEditMultipleObjects]
    public class VRStageLighting_DMX_RealtimeLightEditor : Editor
    {
        GUIStyle _sectionLabel;

        // Fixture Type
        SerializedProperty _fixtureType;

        // DMX Settings
        SerializedProperty _enableDMXChannels;
        SerializedProperty _enableFineChannels;
        SerializedProperty _use5ChannelMode;
        SerializedProperty _useLegacySectorMode;
        SerializedProperty _sector;
        SerializedProperty _dmxChannel;
        SerializedProperty _dmxUniverse;

        // General Settings
        SerializedProperty _maxIntensity;
        SerializedProperty _finalIntensity;
        SerializedProperty _globalIntensity;
        SerializedProperty _curveMod;
        SerializedProperty _isPointLight;
        SerializedProperty _enableConeWidth;
        SerializedProperty _minSpotAngle;
        SerializedProperty _maxSpotAngle;
        SerializedProperty _range;
        SerializedProperty _emitterDepth;
        SerializedProperty _lensTransform;
        SerializedProperty _lightOriginOffset;

        // Movement Settings
        SerializedProperty _enablePanTilt;
        SerializedProperty _invertPan;
        SerializedProperty _invertTilt;
        SerializedProperty _maxMinPan;
        SerializedProperty _maxMinTilt;
        SerializedProperty _panOffset;
        SerializedProperty _tiltOffset;

        // Fixture Settings
        SerializedProperty _enableStrobe;
        SerializedProperty _enableGobo;
        SerializedProperty _enableGoboSpin;

        // Light Output Axis
        SerializedProperty _localLightDirection;

        // Fixture Shell
        SerializedProperty _fixtureShellRenderers;
        SerializedProperty _shellEmissionTint;

        static GUIStyle MakeSectionLabel()
        {
            var g = new GUIStyle
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
            };
            g.normal.textColor = Color.white;
            return g;
        }

        void OnEnable()
        {
            _sectionLabel = MakeSectionLabel();

            _fixtureType         = serializedObject.FindProperty("fixtureType");

            _enableDMXChannels   = serializedObject.FindProperty("enableDMXChannels");
            _enableFineChannels  = serializedObject.FindProperty("enableFineChannels");
            _use5ChannelMode     = serializedObject.FindProperty("use5ChannelMode");
            _useLegacySectorMode = serializedObject.FindProperty("useLegacySectorMode");
            _sector              = serializedObject.FindProperty("sector");
            _dmxChannel          = serializedObject.FindProperty("dmxChannel");
            _dmxUniverse         = serializedObject.FindProperty("dmxUniverse");

            _maxIntensity        = serializedObject.FindProperty("maxIntensity");
            _finalIntensity      = serializedObject.FindProperty("finalIntensity");
            _globalIntensity     = serializedObject.FindProperty("globalIntensity");
            _curveMod            = serializedObject.FindProperty("curveMod");
            _isPointLight        = serializedObject.FindProperty("isPointLight");
            _enableConeWidth     = serializedObject.FindProperty("enableConeWidth");
            _minSpotAngle        = serializedObject.FindProperty("minSpotAngle");
            _maxSpotAngle        = serializedObject.FindProperty("maxSpotAngle");
            _range               = serializedObject.FindProperty("range");
            _emitterDepth        = serializedObject.FindProperty("emitterDepth");
            _lensTransform       = serializedObject.FindProperty("lensTransform");
            _lightOriginOffset   = serializedObject.FindProperty("lightOriginOffset");

            _enablePanTilt       = serializedObject.FindProperty("enablePanTilt");
            _invertPan           = serializedObject.FindProperty("invertPan");
            _invertTilt          = serializedObject.FindProperty("invertTilt");
            _maxMinPan           = serializedObject.FindProperty("maxMinPan");
            _maxMinTilt          = serializedObject.FindProperty("maxMinTilt");
            _panOffset           = serializedObject.FindProperty("panOffset");
            _tiltOffset          = serializedObject.FindProperty("tiltOffset");

            _enableStrobe        = serializedObject.FindProperty("enableStrobe");
            _enableGobo          = serializedObject.FindProperty("enableGobo");
            _enableGoboSpin      = serializedObject.FindProperty("enableGoboSpin");

            _localLightDirection = serializedObject.FindProperty("localLightDirection");

            _fixtureShellRenderers = serializedObject.FindProperty("fixtureShellRenderers");
            _shellEmissionTint     = serializedObject.FindProperty("shellEmissionTint");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            VRSL_EditorHeader.Draw();

            // ── Fixture Type ──────────────────────────────────────────────────
            // Top-level archetype dropdown — drives section visibility below.
            GUILayout.Label("Fixture Type", _sectionLabel);
            EditorGUILayout.PropertyField(_fixtureType, new GUIContent("Type", _fixtureType.tooltip));

            var type = (DMXFixtureType)_fixtureType.enumValueIndex;
            bool isMover     = type == DMXFixtureType.MoverSpotlight
                            || type == DMXFixtureType.MoverWashlight;
            bool showMovement = isMover || type == DMXFixtureType.Custom;
            bool showGobo     = type == DMXFixtureType.MoverSpotlight
                             || type == DMXFixtureType.Custom;
            // Static point light: omnidirectional archetype — no spot/cone/pan-tilt/gobo.
            bool isStaticPoint = type == DMXFixtureType.StaticPointLight;

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── DMX Settings ──────────────────────────────────────────────────
            GUILayout.Label("DMX Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_enableDMXChannels);
            EditorGUILayout.PropertyField(_enableFineChannels);
            EditorGUILayout.PropertyField(_use5ChannelMode, new GUIContent("Use 5 Channel Mode", _use5ChannelMode.tooltip));
            EditorGUILayout.PropertyField(_useLegacySectorMode);
            if (_useLegacySectorMode.boolValue)
            {
                EditorGUILayout.PropertyField(_sector);
            }
            else
            {
                EditorGUILayout.PropertyField(_dmxChannel);
                EditorGUILayout.PropertyField(_dmxUniverse);
            }

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── General Settings ──────────────────────────────────────────────
            GUILayout.Label("General Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_maxIntensity);
            EditorGUILayout.PropertyField(_finalIntensity);
            EditorGUILayout.PropertyField(_globalIntensity);
            EditorGUILayout.PropertyField(_curveMod);
            // StaticPointLight implies point emission (forced in the manager), so the
            // toggle is hidden — showing it would suggest the mode is optional here.
            if (!isStaticPoint)
                EditorGUILayout.PropertyField(_isPointLight);

            // Cone-width controls. Spotlights (and Custom) have a zoom motor on
            // DMX channel +4, so they expose enableConeWidth + min/max angles.
            // Washlights and statics have no zoom motor — lock to max angle and
            // present a single "Spot Angle" field. enableConeWidth is forced to
            // false on those types so the compute shader's lerp is a no-op even
            // if a prefab still serialises the toggle as true.
            bool hasZoomMotor = type == DMXFixtureType.MoverSpotlight
                             || type == DMXFixtureType.Custom;
            if (hasZoomMotor)
            {
                EditorGUILayout.PropertyField(_enableConeWidth);
                EditorGUILayout.PropertyField(_minSpotAngle);
                EditorGUILayout.PropertyField(_maxSpotAngle);
            }
            else
            {
                if (_enableConeWidth.boolValue)
                    _enableConeWidth.boolValue = false;
                // A point light is omnidirectional — no cone angle to author.
                if (!isStaticPoint)
                    EditorGUILayout.PropertyField(
                        _maxSpotAngle,
                        new GUIContent("Spot Angle", _maxSpotAngle.tooltip));
            }
            EditorGUILayout.PropertyField(_range);
            // Emitter depth only meaningfully affects spot cones; hide for point lights
            // since the math collapses back to a point source regardless.
            if (!_isPointLight.boolValue && !isStaticPoint)
                EditorGUILayout.PropertyField(_emitterDepth);
            // lensTransform is a spot-cone anchor; for the omnidirectional StaticPointLight
            // archetype the origin comes from the shell-mesh centre + lightOriginOffset, so
            // hide the lens field for it. lightOriginOffset stays available for all types.
            if (!isStaticPoint)
                EditorGUILayout.PropertyField(_lensTransform);
            EditorGUILayout.PropertyField(_lightOriginOffset);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Movement Settings ─────────────────────────────────────────────
            // Movers (Spotlight, Washlight) and Custom show pan/tilt range and
            // inversion. Static fixtures hide this section since they don't pan/tilt.
            if (showMovement)
            {
                GUILayout.Label("Movement Settings", _sectionLabel);
                EditorGUILayout.PropertyField(_enablePanTilt);
                EditorGUILayout.PropertyField(_invertPan);
                EditorGUILayout.PropertyField(_invertTilt);
                EditorGUILayout.PropertyField(
                    _maxMinPan,
                    new GUIContent("Min/Max Pan Range", _maxMinPan.tooltip));
                EditorGUILayout.PropertyField(
                    _maxMinTilt,
                    new GUIContent("Min/Max Tilt Range", _maxMinTilt.tooltip));
                EditorGUILayout.PropertyField(_panOffset);
                EditorGUILayout.PropertyField(_tiltOffset);

                EditorGUILayout.Space();
                EditorGUILayout.Space();
            }

            // ── Fixture Settings ──────────────────────────────────────────────
            // Strobe is universal (all fixture types support it). Gobo is split
            // into its own conditional section below for spotlight-only display.
            GUILayout.Label("Fixture Settings", _sectionLabel);
            EditorGUILayout.PropertyField(_enableStrobe);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // ── Gobo Settings ─────────────────────────────────────────────────
            // Spotlights and Custom show gobo selection. Washlights, Blinders,
            // and ParLights don't have gobos in the underlying optics — hide.
            if (showGobo)
            {
                GUILayout.Label("Gobo Settings", _sectionLabel);
                EditorGUILayout.PropertyField(_enableGobo);
                EditorGUILayout.PropertyField(_enableGoboSpin);

                EditorGUILayout.Space();
                EditorGUILayout.Space();
            }

            // ── Light Output Axis ─────────────────────────────────────────────
            // Omnidirectional point lights ignore the output axis — hide it.
            if (!isStaticPoint)
            {
                GUILayout.Label("Light Output Axis", _sectionLabel);
                EditorGUILayout.PropertyField(_localLightDirection);

                EditorGUILayout.Space();
                EditorGUILayout.Space();
            }

            // ── Fixture Shell ─────────────────────────────────────────────────
            GUILayout.Label("Fixture Shell", _sectionLabel);
            EditorGUILayout.PropertyField(_fixtureShellRenderers, true);
            EditorGUILayout.PropertyField(_shellEmissionTint);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
