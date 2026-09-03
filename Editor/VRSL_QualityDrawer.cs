using System;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Draws a <see cref="VRSLQuality"/> field with the levels a scene may choose.
    ///
    /// <see cref="VRSLQuality.Low"/> exists for secondary cameras under the Reduced
    /// policy and is not a level to author a scene at, so it is left out of the
    /// popup. A field already holding it, which only a script can arrange, still
    /// shows it rather than silently reading as something else.
    /// </summary>
    [CustomPropertyDrawer(typeof(VRSLQuality))]
    sealed class VRSLQualityDrawer : PropertyDrawer
    {
        static readonly VRSLQuality[] Offered = { VRSLQuality.Off, VRSLQuality.Standard, VRSLQuality.High };

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var current = (VRSLQuality)property.intValue;
            bool listed  = Array.IndexOf(Offered, current) >= 0;

            var values = listed ? Offered : new[] { current, VRSLQuality.Off, VRSLQuality.Standard, VRSLQuality.High };
            var names  = new GUIContent[values.Length];
            for (int i = 0; i < values.Length; i++)
                names[i] = new GUIContent(values[i] == VRSLQuality.Low
                                              ? "Low (mirrors only)"
                                              : values[i].ToString());

            EditorGUI.BeginProperty(position, label, property);
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            int index = Array.IndexOf(values, current);
            EditorGUI.BeginChangeCheck();
            index = EditorGUI.Popup(position, label, index, names);
            if (EditorGUI.EndChangeCheck() && index >= 0 && index < values.Length)
                property.intValue = (int)values[index];
            EditorGUI.showMixedValue = false;
            EditorGUI.EndProperty();
        }
    }
}
