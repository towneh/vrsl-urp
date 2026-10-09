#if UNITY_EDITOR
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The DMX manager's DMX Input and Grid Textures, and how a manager saved with
    /// 0.2.0-rc.6 comes across to them.
    /// </summary>
    public class VRSLGridLayoutTests
    {
        // Each layout's main decode texture as the package ships it.
        const string HorizontalMain = "555ce9e464c9a54c9d331e0030c258ef";
        const string VerticalMain   = "78c70f9c712bd81bf5c13a40970fd79b";
        const string LegacyMain     = "17f83f063191dbffbaae01943cc9c235";

        static string GuidOf(Object asset) =>
            asset == null ? "(empty)" : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));

        /// <summary>A switched-off manager carrying an rc.6 main texture, staged the
        /// way one deserialises: through the hidden field.</summary>
        static VRSL_URPLightManager StageRc6(GameObject go, Object oldMain)
        {
            go.SetActive(false);
            var manager = go.AddComponent<VRSL_URPLightManager>();
            var so = new SerializedObject(manager);
            so.FindProperty(VRSLGridUpgrade.OldField).objectReferenceValue = oldMain;
            so.ApplyModifiedProperties();
            return manager;
        }

        static Object OldMain(VRSL_URPLightManager manager) =>
            new SerializedObject(manager).FindProperty(VRSLGridUpgrade.OldField).objectReferenceValue;

        static RenderTexture Shipped(string guid) =>
            AssetDatabase.LoadAssetAtPath<RenderTexture>(AssetDatabase.GUIDToAssetPath(guid));

        public enum UpgradePath { Enable, Validate, BuildStep }

        /// <summary>
        /// G1. An rc.6 manager comes up in the layout its decode textures were, through
        /// each path the upgrade runs on, and reads that layout's set from the shipped
        /// asset afterwards.
        /// </summary>
        [UnityTest]
        public IEnumerator AnRc6ManagerComesUpInItsOwnLayout(
            [Values(VRSLGridLayout.Horizontal, VRSLGridLayout.Vertical, VRSLGridLayout.Legacy)]
            VRSLGridLayout layout,
            [Values] UpgradePath path)
        {
            string main = layout switch
            {
                VRSLGridLayout.Vertical => VerticalMain,
                VRSLGridLayout.Legacy   => LegacyMain,
                _                       => HorizontalMain,
            };

            var go = new GameObject("rc6 manager");
            try
            {
                var manager = StageRc6(go, Shipped(main));
                Assert.IsNotNull(OldMain(manager), "the rc.6 main texture did not stage");

                yield return Run(manager, go, path);

                Assert.AreEqual(layout, manager.GridLayout,
                    $"an rc.6 {layout} manager came up in the wrong layout");
                Assert.IsNull(OldMain(manager), "the upgrade left the rc.6 texture behind");
                Assert.AreEqual(main, GuidOf(manager.LiveTextures.main),
                    "the manager is not reading its layout's main texture");
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// <summary>
        /// G2. A manager that was pointed at textures of its author's own keeps them,
        /// stays Horizontal and says what to do, on every path, until the author gives
        /// it a Grid Textures asset of their own.
        /// </summary>
        [UnityTest]
        public IEnumerator AnRc6ManagerOnItsOwnTexturesWarnsAndKeepsThem([Values] UpgradePath path)
        {
            var go  = new GameObject("rc6 manager, own textures");
            var own = new CustomRenderTexture(4, 4) { name = "an author's own grid" };
            var ownAsset = ScriptableObject.CreateInstance<VRSLGridTextures>();
            try
            {
                var manager = StageRc6(go, own);

                LogAssert.Expect(LogType.Warning, new Regex("grid textures of your own"));
                yield return Run(manager, go, path);

                Assert.AreEqual(VRSLDmxInput.HorizontalGrid, manager.dmxInput);
                Assert.AreSame(own, OldMain(manager),
                    "the upgrade dropped textures it could not carry across");

                // Dealt with: the author's own asset, which the upgrade cannot tell from
                // anything but its not being the package's.
                manager.gridTextures = ownAsset;
                Assert.AreEqual(VRSLGridUpgrade.Outcome.Upgraded, VRSLGridUpgrade.Upgrade(manager));
                Assert.IsNull(OldMain(manager),
                    "the old textures were kept after the author assigned an asset of their own");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(own);
                Object.DestroyImmediate(ownAsset);
            }
        }

        static IEnumerator Run(VRSL_URPLightManager manager, GameObject go, UpgradePath path)
        {
            switch (path)
            {
                case UpgradePath.Enable:
                    go.SetActive(true);
                    yield return null;
                    break;
                case UpgradePath.Validate:
                    // Deferred like the wiring; the tick is driven, since delayCall does
                    // not fire inside a batch-mode play-mode test.
                    VRSLWiring.ResolveOnValidate(manager);
                    VRSLWiring.FlushPending(manager);
                    yield return null;
                    break;
                case UpgradePath.BuildStep:
                    VRSLGridUpgrade.UpgradeScene(go.scene);
                    yield return null;
                    break;
            }
        }
    }
}
#endif
