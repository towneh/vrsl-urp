using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// The DMX buffers bound while no manager owns them: in the editor outside Play, before
    /// a manager starts, and after the last one switches off.
    ///
    /// D3D12 and Vulkan skip any draw whose shader declares a buffer that isn't bound, so
    /// without these every shader that includes the DMX reads (the mover bodies, the
    /// surface and multi-channel bar bodies, lasers, lens flares) vanishes there, where
    /// D3D11 draws it dark. The channel count published beside them is 0, so the shaders
    /// never read them. They are released before each assembly reload and on quit, so no
    /// buffer outlives the domain, and bound again on load.
    /// </summary>
    static class VRSLDMXFallbackBuffers
    {
        static readonly int s_Channels    = Shader.PropertyToID("_VRSLU_DMXChannels");
        static readonly int s_Movement    = Shader.PropertyToID("_VRSLU_DMXMovement");
        static readonly int s_SpinPhase   = Shader.PropertyToID("_VRSLU_DMXSpinPhase");
        static readonly int s_StrobePhase = Shader.PropertyToID("_VRSLU_DMXStrobePhase");

        static GraphicsBuffer s_channels, s_movement, s_spinPhase, s_strobePhase;

        /// <summary>Bind the fallback buffers in place of a manager's.</summary>
        internal static void Bind()
        {
            // A headless server or a -nographics run draws nothing to skip.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            if (s_channels == null || !s_channels.IsValid())
            {
                Release();
                s_channels    = Zeroed(GraphicsBuffer.Target.Raw);
                s_movement    = Zeroed(GraphicsBuffer.Target.Structured);
                s_spinPhase   = Zeroed(GraphicsBuffer.Target.Structured);
                s_strobePhase = Zeroed(GraphicsBuffer.Target.Structured);
            }
            Shader.SetGlobalBuffer(s_Channels,    s_channels);
            Shader.SetGlobalBuffer(s_Movement,    s_movement);
            Shader.SetGlobalBuffer(s_SpinPhase,   s_spinPhase);
            Shader.SetGlobalBuffer(s_StrobePhase, s_strobePhase);
        }

        static GraphicsBuffer Zeroed(GraphicsBuffer.Target target)
        {
            var buffer = new GraphicsBuffer(target, 1, sizeof(float));
            buffer.SetData(new float[1]);
            return buffer;
        }

        static void Release()
        {
            s_channels?.Release();    s_channels    = null;
            s_movement?.Release();    s_movement    = null;
            s_spinPhase?.Release();   s_spinPhase   = null;
            s_strobePhase?.Release(); s_strobePhase = null;
        }

        /// <summary>Both hooks, because the editor and a player each run only one of them.
        /// In the editor, <c>Application.quitting</c> fires on leaving Play, which would
        /// leave edit mode without them, so the editor releases on reload and quit
        /// instead.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        static void OnLoad()
        {
            Bind();
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Release;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Release;
            UnityEditor.EditorApplication.quitting -= Release;
            UnityEditor.EditorApplication.quitting += Release;
#else
            Application.quitting -= Release;
            Application.quitting += Release;
#endif
        }
    }
}
