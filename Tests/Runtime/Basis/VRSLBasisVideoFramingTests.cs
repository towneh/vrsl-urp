using NUnit.Framework;
using UnityEngine;
using VRSL.URP.BasisIntegration;
using Corners = VRSL.URP.BasisIntegration.BasisVideoRenderTextureOutput.Corners;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Baking the video output's corners from a VRSL reader camera. Row B12 of TESTING.md.
    /// </summary>
    class VRSLBasisVideoFramingTests
    {
        // VRSL's stock screen as Unity imports it: two units across, its U running against x.
        static Mesh StockScreenMesh()
        {
            var mesh = new Mesh
            {
                vertices  = new[] { new Vector3(1, -1, 0), new Vector3(-1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) },
                uv        = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            return mesh;
        }

        /// <summary>
        /// B12. The screen and reader camera of VRSL's shipped Horizontal video reader
        /// (VRSL-DMX-USharpVideoReaderScreen-Horizontal: the stock screen mesh turned half round
        /// y at scale 5, the camera rolled a quarter turn at orthographic size 9.6 into a
        /// 104x960 grid, both under a parent scaled 1.92 x 1.08). The bake must give the
        /// corners that framing works out to, which are the setup menu's to within a pixel of
        /// a 1080p frame; with the camera widened past the screen's edges, it must carry on
        /// past them in proportion.
        /// </summary>
        [Test]
        public void B12_the_bake_reads_the_screen_mesh_own_UVs()
        {
            // The prefab's own values, and where they put the screen and camera centres in
            // the parent's scaled frame.
            const float sx = 1.92f, sy = 1.08f, gridAspect = 104f / 960f;
            const float screenScale = 5f, screenX = 0.02f, screenY = 0.25f;
            const float camX = 0.02f, camY = -3.79f, camZ = -6.81f;
            float halfW = screenScale * sx, halfH = screenScale * sy;
            float screenLeft = screenX * sx - halfW, screenBottom = screenY * sy - halfH;

            var parent = new GameObject("VRSL B12 reader");
            var screen = new GameObject("DMXScreenOutput");
            var camera = new GameObject("DMXCamera");
            var mesh   = StockScreenMesh();
            var target = new RenderTexture(104, 960, 0);
            try
            {
                parent.transform.SetPositionAndRotation(new Vector3(-5.5f, 4f, -1.65f), Quaternion.Euler(0f, -90f, 0f));
                parent.transform.localScale = new Vector3(sx, sy, 1f);

                screen.transform.SetParent(parent.transform, false);
                screen.transform.localPosition = new Vector3(screenX, screenY, 0f);
                screen.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                screen.transform.localScale    = Vector3.one * screenScale;
                screen.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = screen.AddComponent<MeshRenderer>();

                camera.transform.SetParent(parent.transform, false);
                camera.transform.localPosition = new Vector3(camX, camY, camZ);
                camera.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);
                camera.transform.localScale    = new Vector3(1f / sx, 1f / sy, 1f);
                var cam = camera.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;

                // The camera's up is the parent's x and its right the parent's -y, so viewport
                // x runs down the screen and viewport y along it.
                Corners Expected(float ortho)
                {
                    float half = ortho * gridAspect;
                    float uLeft  = (camX * sx - ortho - screenLeft) / (2f * halfW);
                    float uRight = (camX * sx + ortho - screenLeft) / (2f * halfW);
                    float vTop    = (camY * sy + half - screenBottom) / (2f * halfH);
                    float vBottom = (camY * sy - half - screenBottom) / (2f * halfH);
                    return new Corners
                    {
                        bl = new Vector2(uLeft, vTop),     br = new Vector2(uLeft, vBottom),
                        tr = new Vector2(uRight, vBottom), tl = new Vector2(uRight, vTop),
                    };
                }

                cam.orthographicSize = 9.6f;
                Assert.IsTrue(BasisVideoRenderTextureOutput.ComputeUVsFromCamera(
                    cam, renderer, target, out var bl, out var br, out var tr, out var tl), "the bake refused");
                AssertCorners(Expected(9.6f), bl, br, tr, tl, "the shipped framing", 1e-4f);
                AssertCorners(Corners.HorizontalStrip(0f, BasisVideoRenderTextureOutput.HorizontalStripHeight),
                              bl, br, tr, tl, "the shipped framing against the setup menu's", 1f / 1080f);

                // Wider than the screen: the corners fall off the mesh and are extrapolated.
                cam.orthographicSize = 10f;
                Assert.IsTrue(BasisVideoRenderTextureOutput.ComputeUVsFromCamera(
                    cam, renderer, target, out bl, out br, out tr, out tl), "the bake refused past the edges");
                AssertCorners(Expected(10f), bl, br, tr, tl, "framing past the screen's edges", 1e-4f);

                // Turned away from the screen: the screen's plane is behind the camera.
                cam.orthographicSize = 9.6f;
                camera.transform.localRotation = Quaternion.Euler(0f, 180f, -90f);
                Assert.IsFalse(BasisVideoRenderTextureOutput.ComputeUVsFromCamera(
                    cam, renderer, target, out _, out _, out _, out _),
                    "the bake took corners from a screen behind the camera");

                // Facing it again, but the mesh without Read/Write. Play mode can't read its
                // arrays, and doing so logs an error, which fails the row.
                camera.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);
                mesh.UploadMeshData(true);
                Assert.IsFalse(mesh.isReadable, "the mesh is still readable, so this case tests nothing");
                Assert.IsFalse(BasisVideoRenderTextureOutput.ComputeUVsFromCamera(
                    cam, renderer, target, out _, out _, out _, out _),
                    "the bake read a mesh without Read/Write in Play mode");
            }
            finally
            {
                Object.DestroyImmediate(parent);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(target);
            }
        }

        static void AssertCorners(Corners want, Vector2 bl, Vector2 br, Vector2 tr, Vector2 tl, string what, float tol)
        {
            void Near(Vector2 w, Vector2 got, string corner) =>
                Assert.That(Vector2.Distance(w, got), Is.LessThan(tol),
                            $"{what}: {corner} baked {got:F5}, expected {w:F5}");
            Near(want.bl, bl, "bottom-left");
            Near(want.br, br, "bottom-right");
            Near(want.tr, tr, "top-right");
            Near(want.tl, tl, "top-left");
        }
    }
}
