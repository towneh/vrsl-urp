#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// The decode in each grid layout and from a channel source, and what the fixtures
    /// read from each. Rows G6 to G8, G10, G16, G17 and N29 to N33 of TESTING.md.
    /// </summary>
    class VRSLGridDecodeTests : VRSLDMXTest
    {
        static Vector3 Rgb(VRSL_URPLightManager.VRSLLightData d)
            => new Vector3(d.colorAndIntensity.x, d.colorAndIntensity.y, d.colorAndIntensity.z);

        /// <summary>The last channel the rig patches: its last fixture's thirteenth.</summary>
        static int PatchedChannels => VRSLDMXRig.ChannelOf(VRSLDMXRig.FixtureCount - 1) + 12;

        static RenderTexture Solid(Color colour)
        {
            var rt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                     { name = "Solid test grid" };
            rt.Create();
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, colour);
            RenderTexture.active = previous;
            return rt;
        }

        /// <summary>
        /// G6. On a Legacy grid carrying the channel buffer's own values, the
        /// compute decodes every channel the rig patches from the texel the shader-driven
        /// fixtures read it from, and so what the buffer holds for every channel the
        /// layout can carry; the realtime fixtures light as they do from the buffer.
        ///
        /// The texels come from the fixtures' include, found on the GPU, so the row
        /// compares the compute's Legacy read with theirs rather than with a second
        /// derivation. Some channels share a texel: a Legacy row holds two sectors, and
        /// on the 50 by 50 decode textures the first sector's 13th channel (u 0.50) and
        /// the second's 1st (u 0.52) are a texel apart on texel edges, which this GPU
        /// settles onto the same texel. That is the layout and the hardware, not the
        /// decode, and another GPU may settle it differently, so the row names no shared
        /// set: for a shared channel the claim is only that the compute reads what the
        /// fixtures read. The compute's 13th-channel correction table is the one known
        /// difference from the fixtures, in every layout (N24 finds it on real video):
        /// for those channels the compute reads the cell 13 below, and the row asserts
        /// exactly that, since it is also where a port goes almost right.
        /// </summary>
        [UnityTest]
        public IEnumerator G6_a_legacy_grid_decodes_what_the_channel_buffer_does()
        {
            var rig = VRSLDMXRig.Build();
            VRSLDMXRig.PaintedGrid painted = null;
            RenderTexture white = null;
            VRSLGridTextures grid = null;
            try
            {
                yield return rig.Step(4);
                rig.Calibrate();
                // Strobe out of the comparison: the grid path takes it from the strobe
                // texture (white, open) and the buffer path from the channel.
                rig.Manager.disableStrobe = true;
                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 2;
                yield return rig.Step(3);

                var viaBuffer = rig.ReadLights();
                var flat = rig.Manager.PublishedChannels.ToArray();
                Assert.GreaterOrEqual(flat.Length, PatchedChannels, "the source published too few channels");
                var values = new float[PatchedChannels];
                for (int i = 0; i < values.Length; i++) values[i] = flat[i] / 255f;

                // The Legacy decode textures are 50 by 50; the painted grid matches them.
                painted = VRSLDMXRig.PaintGrid(values, VRSLGridLayout.Legacy, 50, 50);
                white   = Solid(Color.white);
                grid    = ScriptableObject.CreateInstance<VRSLGridTextures>();
                grid.legacy = new VRSLGridTextures.Set
                {
                    main = painted.Texture, movement = painted.Texture, strobe = white,
                    spinTimer = painted.Texture,
                };
                Debug.Log($"[G6] {painted.Shared.Count} channels share a texel in the Legacy "
                        + "layout on this GPU: "
                        + string.Join(", ", painted.Shared.Select(ch => $"{ch}@{painted.Cells[ch - 1]}")));

                rig.Manager.ChannelSource = null;
                rig.Manager.gridTextures  = grid;
                rig.Manager.dmxInput      = VRSLDmxInput.LegacyGrid;
                yield return rig.Step(3);
                Assert.AreEqual(0, rig.Manager.ChannelCount, "the manager is still reading the channel buffer");

                var read      = rig.ReadGridChannels(PatchedChannels);
                var notAsRead = new List<string>();
                var notAsSent = new List<string>();
                for (int i = 0; i < read.Length; i++)
                {
                    // The compute's correction table reads these from the cell 13 below,
                    // as N24 finds on real video; the fixtures read their own.
                    int from = VRSLDMXRig.ShiftedThirteenth(i + 1) ? i - 13 : i;
                    if (Mathf.Abs(read[i] - painted.Reads[from]) > Half)
                        notAsRead.Add($"ch {i + 1} decoded {read[i] * 255f:F0}, the fixtures read "
                                    + $"{painted.Reads[from] * 255f:F0} for ch {from + 1} from texel {painted.Cells[from]}");
                    if (from == i && !painted.Shared.Contains(i + 1) && Mathf.Abs(read[i] - values[i]) > Half)
                        notAsSent.Add($"ch {i + 1} decoded {read[i] * 255f:F0}, sent {values[i] * 255f:F0}");
                }
                Assert.IsEmpty(notAsRead, $"{notAsRead.Count} channels decode from a different texel "
                                        + "than the fixtures read: " + string.Join("; ", notAsRead));
                Assert.IsEmpty(notAsSent, $"{notAsSent.Count} channels decode wrong from a Legacy grid: "
                                        + string.Join("; ", notAsSent));

                var viaGrid = rig.ReadLights();
                for (int i = 0; i < VRSLDMXRig.FixtureCount; i++)
                {
                    AssertNear(Rgb(viaBuffer[i]), Rgb(viaGrid[i]),
                        $"fixture {i} lights a different colour from a Legacy grid than from the buffer");
                    Assert.AreEqual(viaBuffer[i].colorAndIntensity.w, viaGrid[i].colorAndIntensity.w, 0.01f,
                        $"fixture {i} lights at a different intensity from a Legacy grid than from the buffer");
                }
            }
            finally
            {
                rig.Dispose();
                painted?.Dispose();
                if (white   != null) Object.DestroyImmediate(white);
                if (grid    != null) Object.DestroyImmediate(grid);
            }
        }

        /// <summary>
        /// G7. With a channel source publishing, a manager with no grid textures
        /// at all lights its realtime fixtures: the buffer path reads no grid, so it must
        /// not need one.
        /// </summary>
        [UnityTest]
        public IEnumerator G7_a_channel_source_lights_the_rig_with_no_grid()
        {
            var rig = VRSLDMXRig.Build();
            try
            {
                // Brought up again with nothing wired, which is what a player does with
                // a scene saved that way; in the editor the wiring would fill it back in.
                using (VRSLWiring.SuppressAutoResolve(rig.Manager))
                {
                    rig.Manager.enabled      = false;
                    rig.Manager.gridTextures = null;
                    rig.Manager.enabled      = true;
                }
                rig.Manager.ChannelSource = rig.Source;
                Assert.IsNull(rig.Manager.LiveTextures.main, "the manager still has a grid");

                rig.Source.pattern   = VRSL_SyntheticDMXChannelSource.Pattern.Ramp;
                rig.Source.universes = 4;
                yield return rig.Step(4);
                // Fails here if nothing decoded: Calibrate needs the light data written.
                rig.Calibrate();

                var lights = rig.ReadLights();
                for (int i = 0; i <= 38; i++)
                {
                    int red = VRSLDMXRig.RedChannel(VRSLDMXRig.ChannelOf(i));
                    var expected = new Vector3(VRSLDMXRig.RampAt(red), VRSLDMXRig.RampAt(red + 1),
                                               VRSLDMXRig.RampAt(red + 2));
                    AssertNear(expected, Rgb(lights[i]), $"fixture {i} did not light from the buffer with no grid");
                }
            }
            finally { rig.Dispose(); }
        }
    }
}
#endif
