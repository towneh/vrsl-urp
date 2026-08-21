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

        private void EnsureMaterial()
        {
            if (blitMat != null) return;
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
                return;
            }
            blitMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        }

        private void Update()
        {
            if (Target == null) return;

            Texture src = Player != null ? Player.OutputTexture : null;
            if (src == null)
            {
                if (ClearWhenNoFrame && !cleared)
                {
                    Graphics.Blit(Texture2D.blackTexture, Target);
                    cleared = true;
                }
                return;
            }

            EnsureMaterial();

            // Per-client frame-origin correction. Some Windows GPUs/drivers can't normalize the
            // decoded frame's orientation natively (the D3D11 video-processor mirror is optional),
            // so the player delivers it top-left origin — vertically mirrored vs GPUs that can.
            // The corner UVs are authored GPU-neutral, so mirror each sampled V per client to land
            // on the same content. Matches how BasisVideoMaterialOutput / BasisVideoDisplay fold
            // Player.OutputFrameIsTopLeftOrigin into their own flip; without it the grid comes out
            // flipped wrong on some PCs.
            Vector2 bl = uvBL, br = uvBR, tr = uvTR, tl = uvTL;
            if (Player != null && Player.OutputFrameIsTopLeftOrigin)
            {
                bl.y = 1f - bl.y;
                br.y = 1f - br.y;
                tr.y = 1f - tr.y;
                tl.y = 1f - tl.y;
            }
            BlitUVs(blitMat, src, Target, bl, br, tr, tl);
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
