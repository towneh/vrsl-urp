#if UNITY_EDITOR
using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Rows GW16 to GW19 and GW21 of TESTING.md: what the gobo wheel stores for grey and
    /// colour gobos, read back from the array the lighting samples.
    /// </summary>
    class VRSLGoboColorTests : VRSLDMXTest
    {
        const string DefaultWheel = "Packages/town.mr.vrsl-urp/Runtime/Textures/MoverLightTextures/GOBO/VRSL-GoboWheel-Default.asset";
        const int Size = 64;

        // ── Reading the array back ────────────────────────────────────────────

        /// <summary>A slice's texels as stored, four bytes each in R, G, B, A order.</summary>
        static byte[] ReadSlice(RenderTexture texture, int slice)
        {
            var request = AsyncGPUReadback.Request(texture, 0, 0, texture.width, 0, texture.height, slice, 1);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, $"reading back slice {slice} of {texture.name} failed");
            var data = request.GetData<byte>(0).ToArray();
            var format = texture.graphicsFormat;
            if (format == GraphicsFormat.B8G8R8A8_UNorm || format == GraphicsFormat.B8G8R8A8_SRGB)
                for (int i = 0; i < data.Length; i += 4)
                    (data[i], data[i + 2]) = (data[i + 2], data[i]);
            else
                Assert.IsTrue(format == GraphicsFormat.R8G8B8A8_UNorm || format == GraphicsFormat.R8G8B8A8_SRGB,
                              $"{texture.name} is {format}, which this row doesn't know how to read");
            return data;
        }

        /// <summary>What the lighting samples from a stored byte, in linear.</summary>
        static float Sampled(byte stored, bool srgb)
            => srgb ? Mathf.GammaToLinearSpace(stored / 255f) : stored / 255f;

        /// <summary>A linear value as an sRGB byte, for comparing with a source drawn in sRGB.</summary>
        static float AsSrgbByte(float linear) => Mathf.LinearToGammaSpace(linear) * 255f;

        static Texture2D Made(string name, TextureFormat format, Func<int, int, Color32> pixel, bool linear = false)
        {
            var tex = new Texture2D(Size, Size, format, false, linear) { name = name, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    pixels[y * Size + x] = pixel(x, y);
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        static bool InDisc(int x, int y)
        {
            float dx = x + 0.5f - Size / 2f, dy = y + 0.5f - Size / 2f;
            return dx * dx + dy * dy < (Size * 0.3f) * (Size * 0.3f);
        }

        static bool DeepInside(int x, int y)
        {
            float dx = x + 0.5f - Size / 2f, dy = y + 0.5f - Size / 2f;
            return dx * dx + dy * dy < (Size * 0.2f) * (Size * 0.2f);
        }

        static bool WellOutside(int x, int y)
        {
            float dx = x + 0.5f - Size / 2f, dy = y + 0.5f - Size / 2f;
            return dx * dx + dy * dy > (Size * 0.4f) * (Size * 0.4f);
        }

        // ── GW16 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// GW16. A wheel with no gobo in colour stores every gobo as grey: linear, the
        /// three channels equal, and the red channel byte for byte what the wheel stored
        /// before gobos had colour (the default copy into a linear target). The stock
        /// gobos are loaded as shipped, compressed.
        /// </summary>
        [Test]
        public void GW16_a_wheel_with_no_colour_gobo_stores_every_gobo_as_its_red_mask()
        {
            var wheel = AssetDatabase.LoadAssetAtPath<VRSLGoboWheelAsset>(DefaultWheel);
            Assert.IsNotNull(wheel, DefaultWheel);
            var grey = Made("grey ramp", TextureFormat.RGBA32,
                            (x, y) => { byte v = (byte)(x * 4 + y % 4); return new Color32(v, v, v, 255); });
            var sources = new Texture2D[wheel.gobos.Length + 1];
            Array.Copy(wheel.gobos, sources, wheel.gobos.Length);
            sources[^1] = grey;

            RenderTexture array = null, reference = null;
            try
            {
                Assert.IsTrue(VRSLGoboWheel.Resident(sources), "a stock gobo isn't fully resident in the editor");
                array = VRSLGoboWheel.Build(sources, out int count, out _);
                Assert.AreEqual(sources.Length, count);
                Assert.IsFalse(array.sRGB, "a wheel with no gobo in colour isn't linear, so every existing wheel moves");

                reference = new RenderTexture(new RenderTextureDescriptor(array.width, array.height,
                                                                          RenderTextureFormat.ARGB32, 0) { sRGB = false });
                reference.Create();
                int unequalSlots = 0;
                for (int slice = 0; slice < sources.Length; slice++)
                {
                    Graphics.Blit(sources[slice], reference);
                    var expected = ReadSlice(reference, 0);
                    var stored = ReadSlice(array, slice);
                    int channelsApart = 0, redApart = 0;
                    for (int i = 0; i < stored.Length; i += 4)
                    {
                        if (stored[i + 1] != stored[i] || stored[i + 2] != stored[i]) channelsApart++;
                        if (stored[i] != expected[i]) redApart++;
                    }
                    int sourceApart = 0;
                    for (int i = 0; i < expected.Length; i += 4)
                        if (expected[i + 1] != expected[i] || expected[i + 2] != expected[i]) sourceApart++;
                    if (sourceApart > 0) unequalSlots++;
                    Debug.Log($"[GW16] slot {slice + 1} {sources[slice].name} ({sources[slice].graphicsFormat}): "
                            + $"{sourceApart} texels with unequal channels as copied, {channelsApart} as stored, "
                            + $"{redApart} red bytes differing from the copy");
                    Assert.AreEqual(0, channelsApart, $"slot {slice + 1} ({sources[slice].name}) isn't stored grey");
                    Assert.AreEqual(0, redApart, $"slot {slice + 1} ({sources[slice].name}) isn't the mask it was");
                }
                Debug.Log($"[GW16] {unequalSlots} of {sources.Length} gobos copy with unequal channels");
            }
            finally
            {
                VRSLGoboWheel.Release(ref array);
                if (reference != null) { reference.Release(); Object.DestroyImmediate(reference); }
                Object.DestroyImmediate(grey);
            }
        }

        // ── GW17 ──────────────────────────────────────────────────────────────

        /// <summary>GW17. A single-channel gobo, red-only or alpha-only, is stored as a
        /// white-on-black mask in all three channels.</summary>
        [Test]
        public void GW17_a_single_channel_gobo_is_stored_as_a_grey_mask()
        {
            var red   = Made("R8 disc", TextureFormat.R8,
                             (x, y) => InDisc(x, y) ? new Color32(255, 0, 0, 0) : new Color32(0, 0, 0, 0), linear: true);
            var alpha = Made("Alpha8 disc", TextureFormat.Alpha8,
                             (x, y) => InDisc(x, y) ? new Color32(0, 0, 0, 255) : new Color32(0, 0, 0, 0), linear: true);
            var sources = new[] { red, alpha };
            RenderTexture array = null;
            try
            {
                // Listed in colour too: a single channel has no colour to keep.
                array = VRSLGoboWheel.Build(sources, new[] { red, alpha }, out _, out _);
                Assert.IsFalse(array.sRGB, "single-channel gobos counted as colour");
                for (int slice = 0; slice < sources.Length; slice++)
                {
                    var stored = ReadSlice(array, slice);
                    var s = sources[slice];
                    Debug.Log($"[GW17] {s.name}: centre stored ({stored[(Size / 2 * Size + Size / 2) * 4]}, "
                            + $"{stored[(Size / 2 * Size + Size / 2) * 4 + 1]}, {stored[(Size / 2 * Size + Size / 2) * 4 + 2]})");
                    for (int y = 0; y < Size; y++)
                        for (int x = 0; x < Size; x++)
                        {
                            int i = (y * Size + x) * 4;
                            if (DeepInside(x, y))
                                for (int c = 0; c < 3; c++)
                                    Assert.GreaterOrEqual(stored[i + c], 254,
                                        $"{s.name} at ({x}, {y}) channel {c}: the open part isn't stored white");
                            else if (WellOutside(x, y))
                                for (int c = 0; c < 3; c++)
                                    Assert.LessOrEqual(stored[i + c], 1,
                                        $"{s.name} at ({x}, {y}) channel {c}: the closed part isn't stored black");
                        }
                }
            }
            finally
            {
                VRSLGoboWheel.Release(ref array);
                Object.DestroyImmediate(red);
                Object.DestroyImmediate(alpha);
            }
        }

        // ── GW18 ──────────────────────────────────────────────────────────────

        /// <summary>GW18. A gobo in colour keeps mid-grey and a dark saturated colour to
        /// within one 8-bit sRGB step of what was drawn, as the lighting samples them.</summary>
        [Test]
        public void GW18_a_colour_gobo_keeps_its_colours_within_one_step()
        {
            var mid  = new Color32(128, 128, 128, 255);
            var dark = new Color32(20, 8, 60, 255);
            var tex = Made("mid grey and dark blue", TextureFormat.RGBA32, (x, y) => x < Size / 2 ? mid : dark);
            RenderTexture array = null;
            try
            {
                array = VRSLGoboWheel.Build(new[] { tex }, new[] { tex }, out _, out _);
                Assert.IsTrue(array.sRGB, "a wheel holding a gobo in colour isn't sRGB");
                var stored = ReadSlice(array, 0);
                foreach (var (x, drawn) in new[] { (Size / 4, mid), (3 * Size / 4, dark) })
                {
                    int i = (Size / 2 * Size + x) * 4;
                    var got = new Vector3(AsSrgbByte(Sampled(stored[i], array.sRGB)),
                                          AsSrgbByte(Sampled(stored[i + 1], array.sRGB)),
                                          AsSrgbByte(Sampled(stored[i + 2], array.sRGB)));
                    Debug.Log($"[GW18] drawn ({drawn.r}, {drawn.g}, {drawn.b}), sampled as sRGB ({got.x:F2}, {got.y:F2}, {got.z:F2})");
                    Assert.AreEqual(drawn.r, got.x, 1.0f, $"red of ({drawn.r}, {drawn.g}, {drawn.b})");
                    Assert.AreEqual(drawn.g, got.y, 1.0f, $"green of ({drawn.r}, {drawn.g}, {drawn.b})");
                    Assert.AreEqual(drawn.b, got.z, 1.0f, $"blue of ({drawn.r}, {drawn.g}, {drawn.b})");
                }
            }
            finally
            {
                VRSLGoboWheel.Release(ref array);
                Object.DestroyImmediate(tex);
            }
        }

        // ── GW19 ──────────────────────────────────────────────────────────────

        /// <summary>GW19. A gobo in colour drawn on transparency, white everywhere with its
        /// shape in alpha, passes light only through the shape.</summary>
        [Test]
        public void GW19_a_colour_gobo_on_transparency_passes_only_its_shape()
        {
            var tex = Made("white disc on transparency", TextureFormat.RGBA32,
                           (x, y) => new Color32(255, 255, 255, (byte)(InDisc(x, y) ? 255 : 0)));
            RenderTexture array = null;
            try
            {
                array = VRSLGoboWheel.Build(new[] { tex }, new[] { tex }, out _, out _);
                var stored = ReadSlice(array, 0);
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        int i = (y * Size + x) * 4;
                        if (DeepInside(x, y))
                            Assert.GreaterOrEqual(Mathf.Min(stored[i], Mathf.Min(stored[i + 1], stored[i + 2])), 254,
                                                  $"({x}, {y}) inside the shape isn't stored white");
                        else if (WellOutside(x, y))
                            Assert.LessOrEqual(Mathf.Max(stored[i], Mathf.Max(stored[i + 1], stored[i + 2])), 1,
                                               $"({x}, {y}) outside the shape passes light");
                    }
            }
            finally
            {
                VRSLGoboWheel.Release(ref array);
                Object.DestroyImmediate(tex);
            }
        }

        // ── GW21 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// GW21. A gobo put in colour while the scene runs is rebuilt into the wheel by the
        /// next frame, the wheel turning sRGB; taken out again, by an edit in place and
        /// <see cref="VRSLSceneResources.Invalidate"/>, it is rebuilt grey and linear.
        /// </summary>
        [UnityTest]
        public IEnumerator GW21_putting_a_gobo_in_colour_at_runtime_rebuilds_the_wheel()
        {
            var green = Made("green", TextureFormat.RGBA32, (x, y) => new Color32(0, 255, 0, 255));
            var wheel = ScriptableObject.CreateInstance<VRSLGoboWheelAsset>();
            wheel.gobos = new[] { green };
            try
            {
                using var rig = VRSLDMXRig.Build(fixtures: 1, withSource: false);
                rig.Manager.goboWheel = wheel;
                yield return rig.Step(1);
                var array = VRSLSceneResources.GoboArray;
                Assert.IsNotNull(array, "no wheel was built");
                Assert.IsFalse(array.sRGB, "the wheel is sRGB with no gobo in colour");
                Assert.AreEqual(0, ReadSlice(array, 0)[(Size / 2 * Size + Size / 2) * 4 + 1],
                                "a green gobo not in colour should pass no light (its red mask is black)");

                int builds = VRSLSceneResources.WheelBuilds;
                wheel.colorGobos = new[] { green };
                yield return rig.Step(1);
                array = VRSLSceneResources.GoboArray;
                Assert.Greater(VRSLSceneResources.WheelBuilds, builds, "putting the gobo in colour didn't rebuild the wheel");
                Assert.IsTrue(array.sRGB, "the wheel didn't turn sRGB with a gobo in colour");
                var stored = ReadSlice(array, 0);
                int centre = (Size / 2 * Size + Size / 2) * 4;
                Assert.GreaterOrEqual(stored[centre + 1], 254, "the green gobo in colour isn't stored green");
                Assert.LessOrEqual(stored[centre], 1, "the green gobo in colour carries red");

                builds = VRSLSceneResources.WheelBuilds;
                wheel.colorGobos[0] = null;
                VRSLSceneResources.Invalidate();
                yield return rig.Step(1);
                array = VRSLSceneResources.GoboArray;
                Assert.Greater(VRSLSceneResources.WheelBuilds, builds, "taking the gobo out of colour didn't rebuild the wheel");
                Assert.IsFalse(array.sRGB, "the wheel stayed sRGB with no gobo in colour");
                Assert.AreEqual(0, ReadSlice(array, 0)[centre + 1], "the gobo is still stored in colour");
            }
            finally
            {
                Object.DestroyImmediate(wheel);
                Object.DestroyImmediate(green);
            }
        }
    }
}
#endif
