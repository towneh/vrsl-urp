#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    class VRSLGridLayoutTests : VRSLDMXTest
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

        // ── Update modes ──────────────────────────────────────────────────────

        static VRSLGridTextures ShippedGridTextures() =>
            AssetDatabase.LoadAssetAtPath<VRSLGridTextures>(
                AssetDatabase.GUIDToAssetPath(VRSLWiring.GridTexturesGuid));

        static List<CustomRenderTexture> Crts(VRSLGridTextures.Set set)
        {
            var list = new List<CustomRenderTexture>();
            foreach (var tex in new[] { set.main, set.movement, set.strobe, set.strobeTimer, set.spinTimer })
                if (tex is CustomRenderTexture crt) list.Add(crt);
            return list;
        }

        static List<CustomRenderTexture> AllCrts(VRSLGridTextures asset)
        {
            var all = new List<CustomRenderTexture>();
            foreach (VRSLGridLayout layout in System.Enum.GetValues(typeof(VRSLGridLayout)))
                all.AddRange(Crts(asset.For(layout)));
            return all;
        }

        static Dictionary<CustomRenderTexture, CustomRenderTextureUpdateMode> Snapshot(
            IEnumerable<CustomRenderTexture> crts)
        {
            var modes = new Dictionary<CustomRenderTexture, CustomRenderTextureUpdateMode>();
            foreach (var crt in crts) modes[crt] = crt.updateMode;
            return modes;
        }

        // In the editor an update mode set on an asset is written to the asset, so every
        // row that stages modes puts the originals back however it ends.
        static void Restore(Dictionary<CustomRenderTexture, CustomRenderTextureUpdateMode> modes)
        {
            foreach (var pair in modes) if (pair.Key != null) pair.Key.updateMode = pair.Value;
        }

        static readonly int s_GridLayout = Shader.PropertyToID("_VRSLU_DMXGridLayout");

        /// <summary>
        /// G4, the load half. With every one of the package's fifteen CRTs left
        /// running, a manager coming up runs only its own layout's five. Staged Realtime
        /// first: the package ships them OnDemand, so a clean project would pass this by
        /// doing nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator OnlyTheLiveLayoutsTexturesRunOnceAManagerIsUp(
            [Values(VRSLGridLayout.Horizontal, VRSLGridLayout.Vertical, VRSLGridLayout.Legacy)]
            VRSLGridLayout layout)
        {
            var asset = ShippedGridTextures();
            Assert.IsNotNull(asset, "the shipped Grid Textures asset is not in this project");
            var all = AllCrts(asset);
            Assert.AreEqual(15, all.Count, "the shipped asset should hold fifteen CRTs");
            var original = Snapshot(all);

            var go = new GameObject("update modes row");
            go.SetActive(false);
            try
            {
                foreach (var crt in all) crt.updateMode = CustomRenderTextureUpdateMode.Realtime;

                var manager = go.AddComponent<VRSL_URPLightManager>();
                manager.dmxInput   = (VRSLDmxInput)layout;
                go.SetActive(true);
                yield return null;
                Assert.AreSame(manager, VRSL_URPLightManager.Instance, "the row's manager does not own the singleton");

                var live = Crts(asset.For(layout));
                var wrong = new List<string>();
                foreach (var crt in all)
                {
                    var expected = live.Contains(crt)
                        ? CustomRenderTextureUpdateMode.Realtime
                        : CustomRenderTextureUpdateMode.OnDemand;
                    if (crt.updateMode != expected) wrong.Add($"{crt.name} is {crt.updateMode}, expected {expected}");
                }
                Assert.IsEmpty(wrong, string.Join("; ", wrong));
                Assert.AreEqual((int)layout, Shader.GetGlobalInteger(s_GridLayout),
                    "the layout global does not say which grid the manager reads");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Restore(original);
            }
        }

        /// <summary>
        /// G4, the switch half. With all fifteen staged Realtime again, a switch by
        /// the call and then one by the field (what the inspector or an Undo does in Play
        /// mode) leaves only the new layout's five running, and once the manager lets go
        /// every one is back at Realtime, as staged.
        /// </summary>
        [UnityTest]
        public IEnumerator OnlyTheLiveLayoutsTexturesRunAfterASwitch()
        {
            var asset = ShippedGridTextures();
            Assert.IsNotNull(asset, "the shipped Grid Textures asset is not in this project");
            var all = AllCrts(asset);
            var original = Snapshot(all);

            var go = new GameObject("update modes switch row");
            go.SetActive(false);
            try
            {
                foreach (var crt in all) crt.updateMode = CustomRenderTextureUpdateMode.Realtime;
                var manager = go.AddComponent<VRSL_URPLightManager>();
                go.SetActive(true);
                yield return null;
                Assert.AreSame(manager, VRSL_URPLightManager.Instance, "the row's manager does not own the singleton");

                void OnlyLive(VRSLGridLayout layout, string how)
                {
                    var live = Crts(asset.For(layout));
                    var wrong = new List<string>();
                    foreach (var crt in all)
                    {
                        var expected = live.Contains(crt)
                            ? CustomRenderTextureUpdateMode.Realtime
                            : CustomRenderTextureUpdateMode.OnDemand;
                        if (crt.updateMode != expected) wrong.Add($"{crt.name} is {crt.updateMode}, expected {expected}");
                    }
                    Assert.IsEmpty(wrong, $"after a switch to {layout} {how}: " + string.Join("; ", wrong));
                    Assert.AreEqual((int)layout, Shader.GetGlobalInteger(s_GridLayout),
                        $"the layout global does not follow a switch {how}");
                }

                manager.SetDmxInput((int)VRSLDmxInput.VerticalGrid);
                OnlyLive(VRSLGridLayout.Vertical, "by the call");

                manager.dmxInput   = VRSLDmxInput.LegacyGrid;
                yield return null;
                OnlyLive(VRSLGridLayout.Legacy, "by the field");

                // The field written and the call made in the same frame: the call binds
                // at once rather than leaving it to LateUpdate.
                manager.dmxInput   = VRSLDmxInput.VerticalGrid;
                manager.SetDmxInput((int)VRSLDmxInput.VerticalGrid);
                OnlyLive(VRSLGridLayout.Vertical, "by the field and then the call, in one frame");

                // The field written, then the call asking for the layout already bound:
                // the field goes back, and LateUpdate has nothing to switch to.
                manager.dmxInput   = VRSLDmxInput.HorizontalGrid;
                manager.SetDmxInput((int)VRSLDmxInput.VerticalGrid);
                Assert.AreEqual(VRSLDmxInput.VerticalGrid, manager.dmxInput,
                    "asking for the bound layout left the field on another one");
                yield return null;
                OnlyLive(VRSLGridLayout.Vertical, "back to the bound layout after the field moved");

                manager.enabled = false;
                var notBack = all.Where(c => c.updateMode != CustomRenderTextureUpdateMode.Realtime)
                                 .Select(c => $"{c.name} is {c.updateMode}").ToList();
                Assert.IsEmpty(notBack, "update modes not put back after two switches: " + string.Join("; ", notBack));
            }
            finally
            {
                Object.DestroyImmediate(go);
                Restore(original);
            }
        }

        /// <summary>
        /// G5. Every update mode is back as it was once the managers have let go,
        /// across a hand-over, and the layout global reads Horizontal with nobody left.
        ///
        /// The modes are staged mixed, so putting back "OnDemand" everywhere cannot pass.
        /// The hand-over is what makes the order matter: an owner that handed over before
        /// restoring would leave its successor noting its own changes as the originals,
        /// and the successor's restore would then put those back instead.
        /// </summary>
        [UnityTest]
        public IEnumerator ReleasingPutsEveryModeBackAndTheLayoutGlobalToHorizontal()
        {
            var asset = ShippedGridTextures();
            Assert.IsNotNull(asset, "the shipped Grid Textures asset is not in this project");
            var all = AllCrts(asset);
            var original = Snapshot(all);

            var ownerGo   = new GameObject("update modes owner");
            var standbyGo = new GameObject("update modes standby");
            ownerGo.SetActive(false);
            try
            {
                var cycle = new[]
                {
                    CustomRenderTextureUpdateMode.Realtime,
                    CustomRenderTextureUpdateMode.OnDemand,
                    CustomRenderTextureUpdateMode.OnLoad,
                };
                for (int i = 0; i < all.Count; i++) all[i].updateMode = cycle[i % cycle.Length];
                var staged = Snapshot(all);

                var owner = ownerGo.AddComponent<VRSL_URPLightManager>();
                owner.dmxInput   = VRSLDmxInput.VerticalGrid;
                ownerGo.SetActive(true);
                yield return null;
                Assert.AreSame(owner, VRSL_URPLightManager.Instance, "the owner does not own the singleton");
                Assert.AreEqual((int)VRSLGridLayout.Vertical, Shader.GetGlobalInteger(s_GridLayout));

                // Added switched off and then switched on, the only way to an enabled
                // manager that does not own the singleton: Awake destroys an enabled
                // duplicate outright.
                standbyGo.SetActive(false);
                var standby = standbyGo.AddComponent<VRSL_URPLightManager>();
                standby.enabled = false;
                standby.dmxInput   = VRSLDmxInput.LegacyGrid;
                standbyGo.SetActive(true);
                standby.enabled = true;
                yield return null;

                owner.enabled = false;
                yield return null;
                Assert.AreSame(standby, VRSL_URPLightManager.Instance, "the standby was not handed the singleton");
                Assert.AreEqual((int)VRSLGridLayout.Legacy, Shader.GetGlobalInteger(s_GridLayout),
                    "the hand-over did not republish the layout");

                standby.enabled = false;
                yield return null;
                Assert.IsNull(VRSL_URPLightManager.Instance, "a manager still owns the singleton");
                Assert.AreEqual((int)VRSLGridLayout.Horizontal, Shader.GetGlobalInteger(s_GridLayout),
                    "the layout global still names a layout with no manager running");

                var wrong = new List<string>();
                foreach (var pair in staged)
                    if (pair.Key.updateMode != pair.Value)
                        wrong.Add($"{pair.Key.name} is {pair.Key.updateMode}, was {pair.Value}");
                Assert.IsEmpty(wrong, "update modes not put back: " + string.Join("; ", wrong));
            }
            finally
            {
                Object.DestroyImmediate(standbyGo);
                Object.DestroyImmediate(ownerGo);
                Restore(original);
            }
        }


        // ── The fixtures take the layout from the manager ─────────────────────

        /// <summary>
        /// G9. No fixture shader or include can choose a layout of its own: the
        /// old material flag is gone from everything but the two CRT shaders (each layout
        /// has its own CRT set, so for them the material is the layout), both read-function
        /// includes choose by the manager's global, and every shader that includes the
        /// Built-in one has the global declared. Materials still carrying the old flag's saved
        /// value are not checked: nothing reads that name any more.
        /// </summary>
        [Test]
        public void NoFixtureShaderChoosesALayoutOfItsOwn()
        {
            string root = System.IO.Path.GetFullPath("Packages/town.mr.vrsl-urp/Runtime/Shaders");
            Assert.IsTrue(System.IO.Directory.Exists(root), $"no shader folder at {root}");

            var crtShaders = new[] { "DMXRTShader-StrobeOutput.shader", "DMXRTShader-DMXInterpolation.shader" };
            var files = System.IO.Directory.EnumerateFiles(root, "*.*", System.IO.SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".shader") || f.EndsWith(".hlsl") || f.EndsWith(".cginc") || f.EndsWith(".compute"))
                .ToList();

            var wrong = new List<string>();
            foreach (var file in files)
            {
                string name = System.IO.Path.GetFileName(file);
                string text = System.IO.File.ReadAllText(file);
                if (text.Contains("_EnableCompatibilityMode") && !crtShaders.Contains(name))
                    wrong.Add($"{name} still names _EnableCompatibilityMode");
                if (text.Contains("_EnableVerticalMode"))
                    wrong.Add($"{name} still names _EnableVerticalMode");

                // The URP read include brings its Defines file in itself; the Built-in one
                // leaves the declaration to whoever includes it.
                bool readsGrid = text.Contains("VRSL-DMXFunctions.cginc");
                bool declares  = text.Contains("VRSL-Defines.cginc") || text.Contains("int _VRSLU_DMXGridLayout");
                if (readsGrid && !declares)
                    wrong.Add($"{name} includes the Built-in read functions without the layout global declared");
            }

            foreach (var include in new[] { "VRSL-DMXFunctions-URP.hlsl", "VRSL-DMXFunctions.cginc" })
            {
                var file = files.FirstOrDefault(f => System.IO.Path.GetFileName(f) == include);
                Assert.IsNotNull(file, $"{include} is not in the package");
                Assert.That(System.IO.File.ReadAllText(file), Does.Contain("_VRSLU_DMXGridLayout == 2"),
                    $"{include} does not choose its read from the manager's layout");
            }

            Assert.IsFalse(files.Any(f => System.IO.Path.GetFileName(f) == "VRSLDMX.cginc"),
                "VRSLDMX.cginc, a third copy of the read functions that nothing includes, is back");
            Assert.IsEmpty(wrong, string.Join("; ", wrong));
        }
    }
}
#endif
