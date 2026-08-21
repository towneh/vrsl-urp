using UnityEngine;
#if VRSL_CILBOX_PRESENT
using Cilbox;
#endif

namespace VRSL.URP.BasisIntegration
{
    // Zero-copy bridge from BasisMediaPlayer to the VRSL DMX decode chain. Publishes the
    // player's live OutputTexture as a global shader texture by reference and republishes
    // only when the texture identity changes (resize / re-open). The native decoder updates
    // the same texture in place every frame, so the interpolation CRT samples the freshest
    // grid each update with no capture camera and no per-frame copy.
#if VRSL_CILBOX_PRESENT
    [Cilboxable]
#endif
    [DisallowMultipleComponent]
    public class BasisVideoToVRSLDMX : MonoBehaviour
    {
        [Tooltip("Player to read from. Assign the BasisMediaPlayer decoding the DMX-over-video stream.")]
        public BasisMediaPlayer Player;

        [Tooltip("Global shader texture the decode chain reads its RAW grid input from. The player's OutputTexture is published here by reference.")]
        public string GlobalTextureName = "";

        private int globalId;
        private bool hasGlobal;
        private Texture bound;

        private void Start()
        {
            if (Player == null)
            {
                Debug.LogWarning("BasisVideoToVRSLDMX: no BasisMediaPlayer assigned.");
                return;
            }
            hasGlobal = !string.IsNullOrEmpty(GlobalTextureName);
            if (hasGlobal) globalId = Shader.PropertyToID(GlobalTextureName);
            Bind(Player.OutputTexture);
        }

        private void Update()
        {
            if (Player != null) Bind(Player.OutputTexture);
        }

        private void Bind(Texture texture)
        {
            if (texture == null || texture == bound) return;
            bound = texture;
            if (hasGlobal) Shader.SetGlobalTexture(globalId, texture);
        }
    }
}
