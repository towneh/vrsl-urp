using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
#if VRSL_CILBOX_PRESENT
using Cilbox;
#endif

namespace VRSL.URP.BasisIntegration
{
    // RenderTexture sink for BasisMediaPlayer that reproduces the framing the VRSL DMX
    // capture camera did, without a camera. Each frame it draws the player's OutputTexture
    // into Target sampled at four source UVs (one per Target corner), so crop, camera roll,
    // flip and shear are all expressible. Set the four UVs by dragging the corners over the
    // live grid in the inspector, or bake them from the old DMX camera as a starting point.
#if VRSL_CILBOX_PRESENT
    [Cilboxable]
#endif
    [DisallowMultipleComponent]
    [AddComponentMenu("VRSL-URP/VRSL Basis Video RT Output")]
    public class BasisVideoRenderTextureOutput : MonoBehaviour
    {
        [Tooltip("Player to read from. If unassigned, GetComponentInParent<BasisMediaPlayer>() is used.")]
        public BasisMediaPlayer Player;

        [Tooltip("RenderTexture that receives the decoded, framed grid. The RAW DMX-grid RT the decode chain reads (the DMX camera's old Target Texture).")]
        public RenderTexture Target;

        // Source UVs sampled at Target's corners. Identity = full frame. Drag in the inspector.
        public Vector2 uvBL = new Vector2(0f, 0f);
        public Vector2 uvBR = new Vector2(1f, 0f);
        public Vector2 uvTR = new Vector2(1f, 1f);
        public Vector2 uvTL = new Vector2(0f, 1f);

        [Tooltip("Write the grid for whichever layout the DMX manager reads, into that layout's own "
               + "raw grid texture, framed with that layout's corners below, so switching the "
               + "manager's DMX Input between grids switches this with it. Off: always write the "
               + "DMX Grid RT with the corners above. Either way nothing is written while the "
               + "manager reads Stream data.")]
        public bool followManagerLayout;

        /// <summary>Four source UVs, one per corner of the grid written.</summary>
        [Serializable]
        public struct Corners
        {
            public Vector2 bl, br, tr, tl;

            public static Corners FullFrame => new Corners
            {
                bl = new Vector2(0f, 0f), br = new Vector2(1f, 0f),
                tr = new Vector2(1f, 1f), tl = new Vector2(0f, 1f),
            };
        }

        // Read while following the manager's layout, one set per layout, since each
        // layout's grid sits in its own place in the video.
        public Corners horizontal = Corners.FullFrame;
        public Corners vertical   = Corners.FullFrame;
        public Corners legacy     = Corners.FullFrame;

        /// <summary>The corners used for <paramref name="layout"/> while following.</summary>
        public Corners CornersFor(VRSLGridLayout layout) => layout switch
        {
            VRSLGridLayout.Vertical => vertical,
            VRSLGridLayout.Legacy   => legacy,
            _                       => horizontal,
        };

        /// <summary>The corners used with following off.</summary>
        public Corners Single => new Corners { bl = uvBL, br = uvBR, tr = uvTR, tl = uvTL };

        [Tooltip("Edit-mode setup aid: a still image of the DMX grid to drag the corners over when not in Play mode. In Play mode the live decoded frame is shown instead.")]
        public Texture SetupPreview;

        [Tooltip("Clear Target to black when no frame is available (before first frame / after Stop) so the decode chain doesn't latch the last grid.")]
        public bool ClearWhenNoFrame = true;

        [Tooltip("DMX camera to bake the framing from (editor only). Can be deleted after baking.")]
        public Camera SourceCamera;

        [Tooltip("Screen quad the DMX camera filmed (editor only). Can be deleted after baking.")]
        public Renderer SourceScreen;

        internal const string BlitShaderName = "Hidden/VRSL-URP/BasisVideoUVBlit";

        [SerializeField, HideInInspector] private Shader blitShader;

        private Material blitMat;
        private bool cleared;
        private bool warnedNoShader;
        private bool warnedNoRawGrid;
        // What the last frame wrote, so a layout switch can blank the grid it leaves.
        private RenderTexture written;

        static readonly int s_DMXTexture = Shader.PropertyToID("_DMXTexture");

        /// <summary>
        /// The raw grid a layout's decode chain reads: the texture its main decode
        /// texture's material samples, which is where the video has to be framed. Null
        /// when the main texture is not a decode texture with one (an author's own).
        /// </summary>
        public static RenderTexture RawGridOf(in VRSLGridTextures.Set set)
        {
            if (set.main is not CustomRenderTexture crt || crt.material == null
                || !crt.material.HasProperty(s_DMXTexture)) return null;
            return crt.material.GetTexture(s_DMXTexture) as RenderTexture;
        }

        /// <summary>What a frame writes.</summary>
        internal enum Write
        {
            /// <summary>Stream data: the manager reads no grid, so nothing is written.</summary>
            Nothing,
            /// <summary>The DMX Grid RT with the single corner set.</summary>
            Target,
            /// <summary>The live layout's raw grid with that layout's corners.</summary>
            LayoutGrid,
            /// <summary>Following, but the live layout has no raw grid to find: the DMX
            /// Grid RT with that layout's corners.</summary>
            TargetForLayout,
        }

        /// <summary>
        /// Where a frame goes and with which corners, from what the manager reads.
        /// <paramref name="layout"/> is null under Stream data; with no manager at all
        /// (<paramref name="hasManager"/> false) there is no layout to follow and the DMX
        /// Grid RT is written with the single corner set.
        /// </summary>
        internal static Write Choose(bool follow, bool hasManager, VRSLGridLayout? layout,
                                     RenderTexture layoutGrid, RenderTexture target,
                                     in Corners single, in Corners forLayout,
                                     out RenderTexture destination, out Corners corners)
        {
            destination = null;
            corners     = single;
            if (hasManager && layout == null) return Write.Nothing;
            if (!follow || !hasManager)
            {
                destination = target;
                return Write.Target;
            }
            corners = forLayout;
            if (layoutGrid != null)
            {
                destination = layoutGrid;
                return Write.LayoutGrid;
            }
            destination = target;
            return Write.TargetForLayout;
        }

        private void Reset()
        {
            if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
            // Adding the component is a user edit; drawing its inspector is not,
            // and filling this in from a draw call dirties the scene for anyone
            // who merely selects the object. Update() finds the shader by name
            // anyway, so an empty field costs nothing.
            if (blitShader == null) blitShader = Shader.Find(BlitShaderName);
        }

        private void Start()
        {
            if (Player == null) Player = GetComponentInParent<BasisMediaPlayer>();
            if (Player == null)
            {
                Debug.LogWarning("BasisVideoRenderTextureOutput: no BasisMediaPlayer assigned or found in parents.");
            }
        }

        private void OnDestroy()
        {
            if (blitMat != null) Destroy(blitMat);
        }

        /// <summary>Blanks Target if this component is the one that owns it.
        /// ClearWhenNoFrame is that claim of ownership, so a scene that leaves
        /// the RT to something else keeps its own arrangement here too.</summary>
        private void ClearTarget(RenderTexture destination)
        {
            if (!ClearWhenNoFrame || cleared || destination == null) return;
            Graphics.Blit(Texture2D.blackTexture, destination);
            cleared = true;
        }

        private bool EnsureMaterial()
        {
            if (blitMat != null) return true;
            // The serialized reference is what pulls the shader into a player
            // build; nothing else refers to it, so a build made with this field
            // empty strips the shader and Shader.Find then answers nothing.
            var sh = blitShader != null ? blitShader : Shader.Find(BlitShaderName);
            if (sh == null)
            {
                // Refusing beats substituting. Any other shader ignores the four
                // corner UVs and blits the whole frame, which fills the grid RT
                // with a picture that decodes into plausible nonsense and says
                // nothing about why.
                if (!warnedNoShader)
                {
                    warnedNoShader = true;
                    Debug.LogError($"[VRSL] \"{BlitShaderName}\" is not available, so the DMX "
                                 + "grid cannot be framed. Assign the blit shader on this "
                                 + "component: an empty field leaves the shader out of a player "
                                 + "build entirely.", this);
                }
                return false;
            }
            blitMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        private void Update()
        {
            // Read each frame rather than told: the manager has no event for a switch,
            // and one property read is all this costs.
            var manager = VRSL_URPLightManager.Instance;
            var layout  = manager != null ? manager.GridLayout : null;
            var live    = followManagerLayout && layout is { } l && manager.gridTextures != null
                        ? manager.gridTextures.For(l) : default;
            var write   = Choose(followManagerLayout, manager != null, layout, RawGridOf(live), Target,
                                 Single, layout is { } c ? CornersFor(c) : Single,
                                 out var destination, out var corners);
            // Under Stream data the decode chain is stopped and nothing reads a grid. The
            // first frame back on a grid writes again, ahead of the chain's first update.
            if (write == Write.Nothing) return;
            if (write == Write.TargetForLayout && !warnedNoRawGrid)
            {
                warnedNoRawGrid = true;
                Debug.LogWarning($"[VRSL] {name} follows the DMX manager's layout, but the {layout} "
                               + "set in its Grid Textures has no raw grid to write into (its main "
                               + "texture isn't one of the package's decode textures), so it writes "
                               + "the DMX Grid RT instead.", this);
            }

            if (destination != written)
            {
                // The grid left behind would otherwise keep its last frame, and its chain
                // reads it the moment that layout is chosen again.
                if (written != null && ClearWhenNoFrame) Graphics.Blit(Texture2D.blackTexture, written);
                written = destination;
                cleared = false;
            }
            if (destination == null) return;

            Texture src = Player != null ? Player.OutputTexture : null;
            if (src == null) { ClearTarget(destination); return; }

            // Nothing framed means the RT keeps whatever it last held, and the
            // decode chain reads it as DMX regardless. Blank it on the same terms
            // as a missing frame rather than leaving the fixtures on a grid this
            // component can no longer refresh.
            if (!EnsureMaterial()) { ClearTarget(destination); return; }

            // Per-client frame-origin correction. Some Windows GPUs/drivers can't normalize the
            // decoded frame's orientation natively (the D3D11 video-processor mirror is optional),
            // so the player delivers it top-left origin — vertically mirrored vs GPUs that can.
            // The corner UVs are authored GPU-neutral, so mirror each sampled V per client to land
            // on the same content. Matches how BasisVideoMaterialOutput / BasisVideoDisplay fold
            // Player.OutputFrameIsTopLeftOrigin into their own flip; without it the grid comes out
            // flipped wrong on some PCs.
            Vector2 bl = corners.bl, br = corners.br, tr = corners.tr, tl = corners.tl;
            if (Player != null && Player.OutputFrameIsTopLeftOrigin)
            {
                bl.y = 1f - bl.y;
                br.y = 1f - br.y;
                tr.y = 1f - tr.y;
                tl.y = 1f - tl.y;
            }
            BlitUVs(blitMat, src, destination, bl, br, tr, tl);
            cleared = false;
        }

        // Whether the blit has to re-encode its sample to sRGB. In linear colour space the
        // sampler converts an sRGB source and the writer converts back only for an sRGB target,
        // so an sRGB source into a linear target arrives curved. The DMX grid RT is linear and
        // carries data rather than a picture, and the curve is not reversible once written at
        // eight bits, so it has to be undone inside the blit while the value is still float.
        // Nothing to do where the two agree, which is the Android path: the player's output is
        // already a linear RenderTexture there.
        static bool NeedsSrgbReencode(Texture src, RenderTexture dst)
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear) return false;
            if (src == null || dst == null) return false;
            return GraphicsFormatUtility.IsSRGBFormat(src.graphicsFormat)
               && !GraphicsFormatUtility.IsSRGBFormat(dst.graphicsFormat);
        }

        // Draws src into dst as a fullscreen quad, sampling src at the four given UVs (one per
        // dst corner). Shared by the runtime path and the editor output preview.
        public static void BlitUVs(Material mat, Texture src, RenderTexture dst, Vector2 bl, Vector2 br, Vector2 tr, Vector2 tl)
        {
            if (mat == null || src == null || dst == null) return;
            mat.SetFloat("_UnSrgb", NeedsSrgbReencode(src, dst) ? 1f : 0f);
            mat.SetVector("_UvBL", new Vector4(bl.x, bl.y, 0f, 0f));
            mat.SetVector("_UvBR", new Vector4(br.x, br.y, 0f, 0f));
            mat.SetVector("_UvTR", new Vector4(tr.x, tr.y, 0f, 0f));
            mat.SetVector("_UvTL", new Vector4(tl.x, tl.y, 0f, 0f));
            Graphics.Blit(src, dst, mat);
        }

        // Projects the camera's viewport corners onto the screen quad's plane and converts
        // the hits to the quad's UV space — a starting point for the four source UVs that the
        // user then fine-tunes by dragging. Returns false if inputs are missing.
        public static bool ComputeUVsFromCamera(Camera cam, Renderer screen, RenderTexture target,
            out Vector2 bl, out Vector2 br, out Vector2 tr, out Vector2 tl)
        {
            bl = Vector2.zero; br = Vector2.right; tr = Vector2.one; tl = Vector2.up;
            if (cam == null || screen == null) return false;

            Transform qt = screen.transform;
            float savedAspect = cam.aspect;
            bool forced = false;
            if (target != null && target.height > 0)
            {
                cam.aspect = target.width / (float)target.height;
                forced = true;
            }

            Vector2[] vp = { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            Vector2[] outUV = new Vector2[4];
            bool ok = true;

            for (int i = 0; i < 4; i++)
            {
                Ray r = cam.ViewportPointToRay(new Vector3(vp[i].x, vp[i].y, 0f));
                Plane plane = new Plane(qt.forward, qt.position);
                if (!plane.Raycast(r, out float enter))
                {
                    plane = new Plane(-qt.forward, qt.position);
                    if (!plane.Raycast(r, out enter)) { ok = false; break; }
                }
                Vector3 local = qt.InverseTransformPoint(r.GetPoint(enter));
                outUV[i] = new Vector2(local.x + 0.5f, local.y + 0.5f);
            }

            if (forced) cam.aspect = savedAspect;
            if (!ok) return false;

            bl = outUV[0]; br = outUV[1]; tr = outUV[2]; tl = outUV[3];
            return true;
        }
    }
}
