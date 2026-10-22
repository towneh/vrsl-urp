#if UNITY_EDITOR
using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows GW4 to GW7 of TESTING.md: the default gobo wheel the package ships, and which
    /// gobos a light manager uses, before and after the editor's first look at it.
    /// </summary>
    class VRSLGoboWheelTests : VRSLDMXTest
    {
        const string Pkg           = "Packages/town.mr.vrsl-urp/";
        const string DefaultWheel  = Pkg + "Runtime/Textures/MoverLightTextures/GOBO/VRSL-GoboWheel-Default.asset";
        const string DmxPrefab     = Pkg + "Runtime/Prefabs/DMX/VRSL-DMX-LightManager.prefab";
        const string AudioLinkPrefab = Pkg + "Runtime/Prefabs/AudioLink/VRSL-AudioLink-LightManager.prefab";

        /// <summary>Upstream VRSL's eight in the order its managers load them, then the
        /// Club Orion logo. Literals, so a reordered asset can't agree with itself.</summary>
        static readonly string[] DefaultNames =
        {
            "VRSL-SpotLightGOBO-Default",
            "VRSL-SpotLightGOBO-5PointStar",
            "VRSL-SpotLightGOBO-8PointStar",
            "VRSL-SpotLightGOBO-ApolloOrb",
            "VRSL-SpotLightGOBO-Spiral",
            "VRSL-SpotLightGOBO-Sticks",
            "VRSL-SpotLightGOBO-Swooshes",
            "VRSL-SpotLightGOBO-Triangles",
            "VRSL-SpotLightGOBO-ClubOrion",
        };

        static VRSLGoboWheelAsset LoadDefault()
        {
            var wheel = AssetDatabase.LoadAssetAtPath<VRSLGoboWheelAsset>(DefaultWheel);
            Assert.IsNotNull(wheel, $"no gobo wheel at {DefaultWheel}");
            return wheel;
        }

        /// <summary>GW4. The default wheel holds upstream's eight in order, then the Club
        /// Orion logo, all the size of upstream's gobos, and both manager prefabs link it
        /// without having been looked at.</summary>
        [Test]
        public void GW4_the_default_wheel_is_upstreams_eight_then_the_Club_Orion_logo()
        {
            var wheel = LoadDefault();
            Assert.IsNotNull(wheel.gobos, "the default wheel has no gobos");
            Assert.AreEqual(DefaultNames.Length, wheel.gobos.Length, "the default wheel holds the wrong number of gobos");
            for (int i = 0; i < DefaultNames.Length; i++)
            {
                var gobo = wheel.gobos[i];
                Assert.IsNotNull(gobo, $"slot {i + 1} of the default wheel is empty");
                Assert.AreEqual(DefaultNames[i], gobo.name, $"slot {i + 1} of the default wheel");
                // One larger gobo would resample every slice up to its size.
                Assert.AreEqual(1024, gobo.width, $"{gobo.name} isn't the size of upstream's gobos");
                Assert.AreEqual(1024, gobo.height, $"{gobo.name} isn't the size of upstream's gobos");
            }

            foreach (var path in new[] { DmxPrefab, AudioLinkPrefab })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.IsNotNull(prefab, path);
                var manager = prefab.GetComponent<IVRSLLightManager>() as MonoBehaviour;
                // Without AudioLink installed its manager's script doesn't load.
                if (manager == null && path == AudioLinkPrefab) continue;
                Assert.IsNotNull(manager, $"{path} carries no light manager");
                var so = new SerializedObject(manager);
                Assert.AreSame(wheel, so.FindProperty(VRSLGoboWheelUpgrade.WheelField).objectReferenceValue,
                               $"{path} doesn't link the default wheel");
                Assert.AreEqual(0, so.FindProperty(VRSLGoboWheelUpgrade.VersionField).intValue,
                                $"{path} says it has been looked at, so no scene's manager ever would be");
                Assert.IsTrue(VRSLGoboWheelUpgrade.IsUpstreamEight(Gobos(so.FindProperty(VRSLGoboWheelUpgrade.ListField))),
                              $"{path}'s own list isn't upstream's eight, so every manager made from it would drop the wheel");
            }
        }

        static Texture2D[] Gobos(SerializedProperty list)
        {
            var gobos = new Texture2D[list.arraySize];
            for (int i = 0; i < gobos.Length; i++)
                gobos[i] = list.GetArrayElementAtIndex(i).objectReferenceValue as Texture2D;
            return gobos;
        }

        static Texture2D[] Plain(int count)
        {
            var gobos = new Texture2D[count];
            for (int i = 0; i < count; i++) gobos[i] = new Texture2D(8, 8) { name = $"plain gobo {i + 1}" };
            return gobos;
        }

        /// <summary>GW5. A manager with no wheel linked builds its wheel from its own list;
        /// linked, from the wheel.</summary>
        [Test]
        public void GW5_a_manager_with_no_wheel_uses_its_own_list()
        {
            var plain = Plain(3);
            try
            {
                using var rig = VRSLDMXRig.Build(fixtures: 1, withSource: false);
                var manager = rig.Manager;
                manager.goboWheel    = null;
                manager.goboTextures = plain;
                manager.RefreshFixtures();
                Assert.AreEqual(3, manager.GoboCount, "with no wheel linked the manager didn't build its own three gobos");

                manager.goboWheel = LoadDefault();
                manager.RefreshFixtures();
                Assert.AreEqual(DefaultNames.Length, manager.GoboCount, "with the default wheel linked the manager didn't build its nine");
            }
            finally
            {
                foreach (var t in plain) Object.DestroyImmediate(t);
            }
        }

        /// <summary>A manager as a scene saved before gobo wheels holds it once its prefab
        /// links the default: wheel linked, never looked at, its own list as authored.
        /// Built switched off so nothing has looked at it yet.</summary>
        static (GameObject root, MonoBehaviour manager) Saved(string prefabPath, Func<Texture2D[], Texture2D[]> list)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.IsNotNull(prefab, prefabPath);
            var root = new GameObject("VRSL saved manager");
            root.SetActive(false);
            var manager = Object.Instantiate(prefab, root.transform).GetComponent<IVRSLLightManager>() as MonoBehaviour;
            var so = new SerializedObject(manager);
            Assert.AreEqual(0, so.FindProperty(VRSLGoboWheelUpgrade.VersionField).intValue, "the prefab's manager has been looked at");
            var prop = so.FindProperty(VRSLGoboWheelUpgrade.ListField);
            var gobos = list(Gobos(prop));
            prop.arraySize = gobos.Length;
            for (int i = 0; i < gobos.Length; i++) prop.GetArrayElementAtIndex(i).objectReferenceValue = gobos[i];
            so.ApplyModifiedPropertiesWithoutUndo();
            return (root, manager);
        }

        static Texture2D[] Resolved(MonoBehaviour manager) => manager switch
        {
            VRSL_DMXURPLightManager dmx => dmx.Gobos,
            _ => (Texture2D[])manager.GetType().GetProperty("Gobos",
                     System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                 .GetValue(manager),
        };

        /// <summary>
        /// GW6. A saved manager whose own list isn't upstream's eight, Club Orion's case
        /// (its logo written into slot 8), keeps that list when it is first switched on:
        /// the default wheel comes off it and its lights index the list.
        /// </summary>
        internal static void KeepsItsOwnList(string prefabPath)
        {
            var logo = AssetDatabase.LoadAssetAtPath<Texture2D>(
                Pkg + "Runtime/Textures/MoverLightTextures/GOBO/VRSL-SpotLightGOBO-ClubOrion.png");
            Assert.IsNotNull(logo, "the Club Orion gobo isn't in the package");
            var (root, manager) = Saved(prefabPath, eight =>
            {
                var club = (Texture2D[])eight.Clone();
                club[7] = logo;
                return club;
            });
            try
            {
                root.SetActive(true);
                var so = new SerializedObject(manager);
                Assert.IsNull(so.FindProperty(VRSLGoboWheelUpgrade.WheelField).objectReferenceValue,
                              "the manager kept the default wheel over gobos of its own");
                Assert.AreEqual(VRSLGoboWheelUpgrade.CurrentVersion, so.FindProperty(VRSLGoboWheelUpgrade.VersionField).intValue,
                                "the manager wasn't marked as looked at");
                var gobos = Resolved(manager);
                Assert.AreEqual(8, gobos.Length, "the manager's lights don't index its own eight");
                Assert.AreSame(logo, gobos[7], "slot 8 isn't the manager's own logo");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void GW6_a_saved_DMX_manager_with_its_own_list_keeps_it() => KeepsItsOwnList(DmxPrefab);

        /// <summary>GW7. A saved manager holding upstream's eight takes the default wheel
        /// and gains its ninth slot.</summary>
        internal static void TakesTheDefault(string prefabPath)
        {
            var (root, manager) = Saved(prefabPath, eight => eight);
            try
            {
                root.SetActive(true);
                var so = new SerializedObject(manager);
                Assert.AreSame(LoadDefault(), so.FindProperty(VRSLGoboWheelUpgrade.WheelField).objectReferenceValue,
                               "a manager holding upstream's eight lost the default wheel");
                Assert.AreEqual(VRSLGoboWheelUpgrade.CurrentVersion, so.FindProperty(VRSLGoboWheelUpgrade.VersionField).intValue,
                                "the manager wasn't marked as looked at");
                Assert.AreEqual(DefaultNames.Length, Resolved(manager).Length, "the manager's lights don't index the default wheel");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void GW7_a_saved_DMX_manager_with_upstreams_eight_takes_the_default_wheel() => TakesTheDefault(DmxPrefab);
    }
}
#endif
