// Editor-only in substance: resolution reads the AssetDatabase, which a player has no
// equivalent of, and R-M7-1 wants the resolved reference serialised so a built player
// never looks anything up. It lives in the runtime assembly regardless, because the suite
// reaches it and the test assembly does not reference the editor one — the same
// arrangement VRSLBenchmarkScene uses for the same reason.
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// One field that has exactly one correct value, and what an author sees when it
    /// does not have it.
    /// </summary>
    class VRSLWiringField
    {
        /// <summary>The serialised field on the manager. Pinned by a row, because a
        /// rename here stops resolution silently — which is the failure this whole
        /// mechanism exists to remove, arriving through its own front door.</summary>
        public string Field;

        /// <summary>This package's asset, by GUID.</summary>
        public string Guid;

        /// <summary>
        /// What the author will see when the field is empty.
        ///
        /// The consequence, not the cause. "Every surface lights as flat grey" is the
        /// register R-M7-5 asks for; "surfacePropertiesShader is null" is the thing it
        /// exists to replace, and the field name belongs after the consequence rather
        /// than instead of it.
        /// </summary>
        public string Consequence;
    }

    /// <summary>
    /// Where the managers' wiring fields point, and what goes wrong without them.
    ///
    /// <b>By GUID, not by name.</b> Every asset in this package had its GUID regenerated
    /// at extraction and shares none with <c>com.acchosen.vr-stage-lighting</c>, which
    /// ships CustomRenderTextures with identical names. Both packages installed together
    /// is a supported configuration, so a name search would not merely be fragile here —
    /// it would resolve to the other package's assets, and a manager reading the legacy
    /// decode chain reports green on every diagnostic while every channel decodes zero.
    /// That is the exact fault this milestone exists to remove.
    /// </summary>
    static class VRSLWiring
    {
        /// <summary>
        /// The DMX manager's ten. Every one has a single correct value, none of them can
        /// be worked out by an author, and each fails without an error.
        /// </summary>
        public static readonly VRSLWiringField[] Dmx =
        {
            new() { Field = "dmxMainTexture",
                    Guid  = "555ce9e464c9a54c9d331e0030c258ef",
                    Consequence = "every channel reads zero, so nothing lights at all" },
            new() { Field = "dmxMovementTexture",
                    Guid  = "63c403376634fef0560b44d7f19aedaa",
                    Consequence = "moving heads stay where they are" },
            new() { Field = "dmxStrobeTexture",
                    Guid  = "e3fa773f91559868d4104ab3ce54bf79",
                    Consequence = "strobe does nothing" },
            // The one the tooltip calls optional and the shipped prefabs leave empty.
            // Optional only in a scene that never strobes: the StrobeOutput CRT's decode
            // shader samples this, so where strobe is used a missing one takes the whole
            // rig dark rather than merely stopping the flash.
            new() { Field = "dmxStrobeTimerTexture",
                    Guid  = "e6641cd38ad1a21be3425ab43ecc2506",
                    Consequence = "every fixture goes dark wherever strobe is used" },
            new() { Field = "dmxSpinTimerTexture",
                    Guid  = "5bee9c07b3305e88be89de0d01fc2f07",
                    Consequence = "gobos stop spinning" },
            new() { Field = "computeShader",
                    Guid  = "213d3149ed0cbc73835aee17d5180332",
                    Consequence = "nothing lights at all" },
            new() { Field = "lightCullShader",
                    Guid  = "5f41dc4adb8e42f8937cb0064c39ea17",
                    Consequence = "the picture is right and every pixel pays for every "
                                + "fixture on screen" },
            new() { Field = "lightingShader",
                    Guid  = "d8c757279dc0dc7ff544149ea9f3e897",
                    Consequence = "no light lands on surfaces" },
            new() { Field = "surfacePropertiesShader",
                    Guid  = "5e926720f16a403487984bfa2578e36f",
                    Consequence = "every surface lights as flat grey, whatever colour it is" },
            new() { Field = "volumetricShader",
                    Guid  = "ee4b25e5a146ba8b36ed731b36047774",
                    Consequence = "no beams in the air" },
        };

        /// <summary>
        /// The AudioLink manager's five. Four are the same assets the DMX manager uses —
        /// the cull, the lighting, the prepass and the volumetric are shared — and only
        /// the compute differs.
        ///
        /// <c>samplingTexture</c> is deliberately not here. It accepts any texture, falls
        /// back to AudioLink's own atlas when empty, and is a palette an author chooses:
        /// content rather than wiring, like <c>goboTextures</c>.
        /// </summary>
        public static readonly VRSLWiringField[] AudioLink =
        {
            new() { Field = "computeShader",
                    Guid  = "b62eba04b10a6cc19700ff31693a9066",
                    Consequence = "nothing lights at all" },
            new() { Field = "lightCullShader",
                    Guid  = "5f41dc4adb8e42f8937cb0064c39ea17",
                    Consequence = "the picture is right and every pixel pays for every "
                                + "fixture on screen" },
            new() { Field = "lightingShader",
                    Guid  = "d8c757279dc0dc7ff544149ea9f3e897",
                    Consequence = "no light lands on surfaces" },
            new() { Field = "surfacePropertiesShader",
                    Guid  = "5e926720f16a403487984bfa2578e36f",
                    Consequence = "every surface lights as flat grey, whatever colour it is" },
            new() { Field = "volumetricShader",
                    Guid  = "ee4b25e5a146ba8b36ed731b36047774",
                    Consequence = "no beams in the air" },
        };

        /// <summary>Every field either manager has, deduplicated by GUID — which is what
        /// a row checking the table against the project wants to walk.</summary>
        public static IEnumerable<VRSLWiringField> All
        {
            get
            {
                var seen = new HashSet<string>();
                foreach (var f in Dmx)       if (seen.Add(f.Guid)) yield return f;
                foreach (var f in AudioLink) if (seen.Add(f.Guid)) yield return f;
            }
        }

        /// <summary>The asset a field points at, or null when the GUID resolves to
        /// nothing in this project.</summary>
        public static Object Load(VRSLWiringField field)
        {
            if (field == null || string.IsNullOrEmpty(field.Guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(field.Guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Object>(path);
        }

        /// <summary>The asset's path, for a message that has to name what it looked
        /// for. Empty when the GUID resolves to nothing.</summary>
        public static string PathOf(VRSLWiringField field) =>
            field == null ? "" : AssetDatabase.GUIDToAssetPath(field.Guid);
    }
}
#endif
