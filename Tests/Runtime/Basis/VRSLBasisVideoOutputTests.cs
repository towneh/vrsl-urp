#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VRSL.URP.BasisIntegration;
using Write   = VRSL.URP.BasisIntegration.BasisVideoRenderTextureOutput.Write;
using Corners = VRSL.URP.BasisIntegration.BasisVideoRenderTextureOutput.Corners;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The Basis video output following the DMX manager's input. Rows E8 and E9 of
    /// TESTING.md.
    /// </summary>
    class VRSLBasisVideoOutputTests : VRSLDMXTest
    {
        // Each layout's raw grid as the package ships it: what its main decode texture's
        // material samples.
        const string HorizontalRaw = "296355125afa6ab0e8f8b24e0b728675";
        const string VerticalRaw   = "07d5a671ce5e33008fe5e83e308e736a";
        const string LegacyRaw     = "c3f2b3d161ba6350bc80b95e71419f18";

        static readonly VRSLGridLayout[] Layouts =
            { VRSLGridLayout.Horizontal, VRSLGridLayout.Vertical, VRSLGridLayout.Legacy };

        static string RawGuid(VRSLGridLayout layout) => layout switch
        {
            VRSLGridLayout.Vertical => VerticalRaw,
            VRSLGridLayout.Legacy   => LegacyRaw,
            _                       => HorizontalRaw,
        };

        static VRSLGridTextures ShippedGrid() =>
            AssetDatabase.LoadAssetAtPath<VRSLGridTextures>(AssetDatabase.GUIDToAssetPath(VRSLWiring.GridTexturesGuid));

        static RenderTexture Raw(VRSLGridLayout layout) =>
            AssetDatabase.LoadAssetAtPath<RenderTexture>(AssetDatabase.GUIDToAssetPath(RawGuid(layout)));

        static Corners Shifted(float by) => new Corners
        {
            bl = new Vector2(by, 0f), br = new Vector2(1f, by), tr = new Vector2(1f - by, 1f), tl = new Vector2(0f, 1f - by),
        };

        /// <summary>
        /// E8, the choice as a pure function. With following on, the live layout's
        /// raw grid and corners; with it off, the DMX Grid RT and the single corners;
        /// under Stream data nothing either way; with no manager, the DMX Grid RT as
        /// before. The raw grid is found from the shipped Grid Textures for every layout,
        /// and an author's main texture that is no decode texture gives none.
        /// </summary>
        [Test]
        public void E8_the_output_writes_the_live_layouts_grid_or_nothing()
        {
            var shipped = ShippedGrid();
            Assert.IsNotNull(shipped, "the shipped Grid Textures asset is not in this project");
            foreach (var layout in Layouts)
                Assert.AreEqual(RawGuid(layout), AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(
                    BasisVideoRenderTextureOutput.RawGridOf(shipped.For(layout)))),
                    $"the {layout} raw grid is not the one its decode chain reads");

            var own = new RenderTexture(4, 4, 0) { name = "an author's own main texture" };
            var target = new RenderTexture(4, 4, 0) { name = "DMX Grid RT" };
            try
            {
                Assert.IsNull(BasisVideoRenderTextureOutput.RawGridOf(new VRSLGridTextures.Set { main = own }));

                var single = Corners.FullFrame;
                var forLayout = Shifted(0.1f);
                var raw = Raw(VRSLGridLayout.Vertical);

                foreach (bool follow in new[] { true, false })
                {
                    Assert.AreEqual(Write.Nothing, BasisVideoRenderTextureOutput.Choose(follow, true, null, raw, target,
                        single, forLayout, out var none, out _), "Stream data wrote something");
                    Assert.IsNull(none);
                    Assert.AreEqual(Write.Target, BasisVideoRenderTextureOutput.Choose(follow, false, null, raw, target,
                        single, forLayout, out var dst, out var c), "with no manager the DMX Grid RT went unwritten");
                    Assert.AreSame(target, dst);
                    Assert.AreEqual(single, c);
                }

                Assert.AreEqual(Write.Target, BasisVideoRenderTextureOutput.Choose(false, true, VRSLGridLayout.Vertical,
                    raw, target, single, forLayout, out var off, out var offCorners));
                Assert.AreSame(target, off);
                Assert.AreEqual(single, offCorners);

                Assert.AreEqual(Write.LayoutGrid, BasisVideoRenderTextureOutput.Choose(true, true, VRSLGridLayout.Vertical,
                    raw, target, single, forLayout, out var on, out var onCorners));
                Assert.AreSame(raw, on);
                Assert.AreEqual(forLayout, onCorners);

                Assert.AreEqual(Write.TargetForLayout, BasisVideoRenderTextureOutput.Choose(true, true, VRSLGridLayout.Vertical,
                    null, target, single, forLayout, out var fallback, out var fallbackCorners));
                Assert.AreSame(target, fallback);
                Assert.AreEqual(forLayout, fallbackCorners);
            }
            finally
            {
                Object.DestroyImmediate(own);
                Object.DestroyImmediate(target);
            }
        }

        static float Brightest(RenderTexture rt)
        {
            var shot = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            var was = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                shot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                shot.Apply(false);
                float max = 0f;
                foreach (var p in shot.GetPixels32()) max = Mathf.Max(max, p.r, Mathf.Max(p.g, p.b));
                return max;
            }
            finally
            {
                RenderTexture.active = was;
                Object.DestroyImmediate(shot);
            }
        }

        static void Blacken(RenderTexture rt) => Graphics.Blit(Texture2D.blackTexture, rt);

        /// <summary>
        /// E9. A real player on the marching fixture, the output following the
        /// manager with the fixture's strip framed in every layout's corner set. Switched
        /// through the three grids, the live layout's raw grid receives the frame and the
        /// other two stay black; under Stream data none does; back on a grid, the frame
        /// the switch lands in writes.
        /// </summary>
        [UnityTest]
        public IEnumerator E9_the_output_follows_the_managers_layout_and_pauses_on_stream_data()
        {
            var scene = VRSLDMXRig.Build(withSource: false);
            var host  = new GameObject("Basis Player");
            host.transform.SetParent(scene.Manager.transform.parent, false);
            var player = host.AddComponent<BasisMediaPlayer>();
            try
            {
                player.playOnStart = false;
                player.liveness = BmLiveness.Vod;
                player.allowLocalAddresses = true;

                var strip = new Corners
                {
                    bl = VRSLBasisDmxOverVideoTests.UvBL, br = VRSLBasisDmxOverVideoTests.UvBR,
                    tr = VRSLBasisDmxOverVideoTests.UvTR, tl = VRSLBasisDmxOverVideoTests.UvTL,
                };
                var video = host.AddComponent<BasisVideoRenderTextureOutput>();
                video.Player = player;
                video.followManagerLayout = true;
                // As the setup menu leaves it, so an output that kept writing under Stream
                // data would have somewhere to write.
                video.Target = Raw(VRSLGridLayout.Horizontal);
                video.horizontal = video.vertical = video.legacy = strip;

                foreach (var layout in Layouts) Blacken(Raw(layout));

                string url = VRSLBasisDmxOverVideoTests.Fixture();
                BasisPlayerApproval.Approve(player, url);
                player.Open(url);
                player.Play();
                float started = Time.realtimeSinceStartup;
                while (player.PresentedFrameCount < 10)
                {
                    Assert.AreNotEqual(BmState.Error, player.State, $"the player errored ({player.ErrorCode})");
                    Assert.Less(Time.realtimeSinceStartup - started, 40f, "the fixture never got going");
                    yield return scene.Step(1);
                }

                foreach (var layout in Layouts)
                {
                    scene.Manager.SetDmxInput((int)layout);
                    yield return scene.Step(3);
                    foreach (var other in Layouts)
                    {
                        float brightest = Brightest(Raw(other));
                        if (other == layout)
                            Assert.Greater(brightest, 0f, $"on {layout} its raw grid received no frame");
                        else
                            Assert.AreEqual(0f, brightest, $"on {layout} the {other} raw grid was written");
                    }
                }

                scene.Manager.SetDmxInput((int)VRSLDmxInput.StreamData);
                yield return scene.Step(1);
                foreach (var layout in Layouts) Blacken(Raw(layout));
                yield return scene.Step(5);
                foreach (var layout in Layouts)
                    Assert.AreEqual(0f, Brightest(Raw(layout)), $"under Stream data the {layout} raw grid was written");

                scene.Manager.SetDmxInput((int)VRSLDmxInput.HorizontalGrid);
                yield return scene.Step(1);
                Assert.Greater(Brightest(Raw(VRSLGridLayout.Horizontal)), 0f,
                    "the first frame after choosing Horizontal again did not write its raw grid");
            }
            finally
            {
                try { player.Stop(); }
                finally
                {
                    foreach (var layout in Layouts) Blacken(Raw(layout));
                    scene.Dispose();
                }
            }
        }
    }
}
#endif
