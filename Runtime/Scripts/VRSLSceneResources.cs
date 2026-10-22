using System.Collections.Generic;
using UnityEngine;

namespace VRSL.URP
{
    /// <summary>
    /// What a light manager offers the scene-wide resources: its gobo wheel and disco
    /// ball cubemap, whether it holds any fixtures, and whether it is the DMX manager,
    /// whose choices win.
    /// </summary>
    internal interface IVRSLSceneResourceUser
    {
        /// <summary>True for the DMX manager. Its wheel and cubemap are the scene's
        /// whenever it has one, because DMX shows are programmed against its gobo
        /// bands.</summary>
        bool TakesPrecedence { get; }

        /// <summary>The gobos this manager's lights index: its wheel asset's, or its own
        /// list when it links none.</summary>
        Texture2D[] Gobos { get; }

        Cubemap DiscoballCubemap { get; }

        /// <summary>Whether this manager draws anything. With no user holding fixtures
        /// nothing samples the wheel, so none is built.</summary>
        bool HoldsFixtures { get; }
    }

    /// <summary>
    /// The resources every VRSL light in the scene samples, whichever manager drew it:
    /// the gobo wheel, the disco ball cubemap and the beam noise. One of each, built once
    /// and bound as shader globals by this class alone, so no manager's camera callback
    /// can bind over another's.
    /// </summary>
    /// <remarks>
    /// Bound with <c>Shader.SetGlobal*</c> outside the render graph, when they change
    /// rather than per camera. Render Graph only clears the globals it published itself,
    /// so these hold across every camera, and shaders outside VRSL's passes see them too.
    ///
    /// The managers join on taking ownership and leave on disable; the last to leave
    /// releases everything. When the managers' wheels differ, the DMX manager's is the
    /// scene's (<see cref="Choose"/>).
    /// </remarks>
    internal static class VRSLSceneResources
    {
        static readonly int s_Gobos         = Shader.PropertyToID("_VRSLGobos");
        static readonly int s_DiscoballCube = Shader.PropertyToID("_VRSLDiscoballCube");
        static readonly int s_CubeBound     = Shader.PropertyToID("_VRSLDiscoballCubeBound");
        static readonly int s_VolNoise      = Shader.PropertyToID("_VRSLVolNoise");

        static readonly List<IVRSLSceneResourceUser> s_users = new();

        // What each user offered when last looked at, by reference, so a field pointed
        // somewhere else is noticed without comparing contents every frame.
        static readonly List<Texture2D[]> s_seenGobos = new();
        static readonly List<Cubemap>     s_seenCubes = new();
        static readonly List<bool>        s_seenHolds = new();

        static bool          s_dirty = true;
        static int           s_preparedFrame = -1;
        static Texture2D[]   s_builtFrom;
        static RenderTexture s_wheel;
        static bool          s_wheelComplete = true;
        static Cubemap       s_cube;
        static Texture       s_noise;
        static ComputeShader s_noiseFrom;

        /// <summary>The scene's wheel, or null with no gobos or no fixtures.</summary>
        public static RenderTexture GoboArray => s_wheel;

        /// <summary>Slices in <see cref="GoboArray"/>. Each light-update compute takes
        /// this: AudioLink lights reach every slice, DMX the first eight.</summary>
        public static int GoboCount { get; private set; }

        /// <summary>The cubemap every disco ball in the scene throws, or null.</summary>
        public static Cubemap DiscoballCubemap => s_cube;

        /// <summary>The density field the beams sample, or null until a manager with
        /// beams has asked for it.</summary>
        public static Texture VolumetricNoise => s_noise;

        /// <summary>Wheel arrays and noise volumes built since load, for the row that
        /// counts them.</summary>
        internal static int WheelBuilds { get; private set; }
        internal static int NoiseBakes  { get; private set; }
        /// <summary>Shader globals set since load, for the row that checks a frame with
        /// nothing changed sets none.</summary>
        internal static int Binds { get; private set; }

        public static void Join(IVRSLSceneResourceUser user)
        {
            if (user == null || s_users.Contains(user)) return;
            s_users.Add(user);
            s_seenGobos.Add(null);
            s_seenCubes.Add(null);
            s_seenHolds.Add(false);
            s_dirty = true;
        }

        public static void Leave(IVRSLSceneResourceUser user)
        {
            int i = s_users.IndexOf(user);
            if (i < 0) return;
            s_users.RemoveAt(i);
            s_seenGobos.RemoveAt(i);
            s_seenCubes.RemoveAt(i);
            s_seenHolds.RemoveAt(i);
            if (s_users.Count == 0) ReleaseAll();
            else                    s_dirty = true;
        }

        /// <summary>Look again at what the managers offer: a fixture came or went, or a
        /// wheel's contents were edited in place.</summary>
        public static void Invalidate() => s_dirty = true;

        /// <summary>
        /// The wheel and cubemap the scene uses from what <paramref name="users"/> offer:
        /// the DMX manager's where it has one, otherwise the other's.
        /// </summary>
        public static (Texture2D[] gobos, Cubemap cube) Choose(IReadOnlyList<IVRSLSceneResourceUser> users)
        {
            Texture2D[] gobos = null;
            Cubemap cube = null;
            bool gobosFirst = false, cubeFirst = false;
            for (int i = 0; i < users.Count; i++)
            {
                var u = users[i];
                if (u == null || !VRSLLightPaths.IsAlive(u)) continue;
                var g = u.Gobos;
                if (g != null && g.Length > 0 && (gobos == null || (u.TakesPrecedence && !gobosFirst)))
                {
                    gobos = g;
                    gobosFirst = u.TakesPrecedence;
                }
                var c = u.DiscoballCubemap;
                if (c != null && (cube == null || (u.TakesPrecedence && !cubeFirst)))
                {
                    cube = c;
                    cubeFirst = u.TakesPrecedence;
                }
            }
            return (gobos, cube);
        }

        /// <summary>Whether two wheels hold the same gobos in the same order.</summary>
        public static bool SameWheel(Texture2D[] a, Texture2D[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            int na = a?.Length ?? 0, nb = b?.Length ?? 0;
            if (na != nb) return false;
            for (int i = 0; i < na; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Bring the wheel and cubemap up to date with the managers, at most once a frame.
        /// Called from each manager's camera callback before it records anything; with
        /// nothing changed it compares a few references and binds nothing.
        /// </summary>
        public static void Prepare()
        {
            int frame = Time.frameCount;
            if (s_preparedFrame == frame) return;
            s_preparedFrame = frame;

            for (int i = 0; i < s_users.Count; i++)
            {
                var u = s_users[i];
                if (!VRSLLightPaths.IsAlive(u)) { s_dirty = true; continue; }
                var g = u.Gobos;
                var c = u.DiscoballCubemap;
                bool h = u.HoldsFixtures;
                if (!ReferenceEquals(g, s_seenGobos[i]) || c != s_seenCubes[i] || h != s_seenHolds[i])
                {
                    s_seenGobos[i] = g;
                    s_seenCubes[i] = c;
                    s_seenHolds[i] = h;
                    s_dirty = true;
                }
            }

            if (s_dirty)
            {
                s_dirty = false;
                Resolve();
            }
            else if (!s_wheelComplete && VRSLGoboWheel.Resident(s_builtFrom))
            {
                // The first build caught a gobo part-way through streaming in.
                BuildWheel(s_builtFrom);
            }
        }

        static void Resolve()
        {
            var (gobos, cube) = Choose(s_users);

            bool anyFixtures = false;
            foreach (var u in s_users)
                if (VRSLLightPaths.IsAlive(u) && u.HoldsFixtures) { anyFixtures = true; break; }
            if (!anyFixtures) gobos = null;

            if (!SameWheel(gobos, s_builtFrom) || (gobos != null && s_wheel == null))
                BuildWheel(gobos);

            if (cube != s_cube)
            {
                s_cube = cube;
                if (cube != null) Shader.SetGlobalTexture(s_DiscoballCube, cube);
                Shader.SetGlobalFloat(s_CubeBound, cube != null ? 1f : 0f);
                Binds++;
            }
        }

        static void BuildWheel(Texture2D[] gobos)
        {
            VRSLGoboWheel.Release(ref s_wheel);
            // A copy, so an author editing the list in place reads as a change.
            s_builtFrom = gobos != null ? (Texture2D[])gobos.Clone() : null;
            s_wheel = VRSLGoboWheel.Build(s_builtFrom, out int count, out s_wheelComplete);
            GoboCount = s_wheel != null ? count : 0;
            if (s_wheel != null)
            {
                WheelBuilds++;
                Binds++;
                Shader.SetGlobalTexture(s_Gobos, s_wheel);
            }
        }

        /// <summary>
        /// Bake the beam noise with <paramref name="compute"/> if the scene has none yet,
        /// and bind it. A manager with beams calls this from its camera callback; once
        /// baked it is a null check.
        /// </summary>
        public static void EnsureVolumetricNoise(ComputeShader compute, Object context)
        {
            // A fallback from a compute that couldn't bake is retried with another.
            if (s_noise != null && (VRSLVolumetricNoise.IsBaked(s_noise) || compute == s_noiseFrom)) return;
            VRSLVolumetricNoise.Release(ref s_noise);
            s_noise = VRSLVolumetricNoise.Bake(compute, context);
            s_noiseFrom = compute;
            if (VRSLVolumetricNoise.IsBaked(s_noise)) NoiseBakes++;
            Binds++;
            Shader.SetGlobalTexture(s_VolNoise, s_noise);
        }

        static void ReleaseAll()
        {
            VRSLGoboWheel.Release(ref s_wheel);
            s_builtFrom = null;
            s_wheelComplete = true;
            GoboCount = 0;
            VRSLVolumetricNoise.Release(ref s_noise);
            s_noiseFrom = null;
            s_cube = null;
            Shader.SetGlobalFloat(s_CubeBound, 0f);
            s_dirty = true;
            s_preparedFrame = -1;
        }

        /// <summary>Statics survive entering Play with domain reload off; anything left
        /// from before is let go.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnLoad()
        {
            s_users.Clear();
            s_seenGobos.Clear();
            s_seenCubes.Clear();
            s_seenHolds.Clear();
            ReleaseAll();
#if UNITY_EDITOR
            VRSLGoboWheelAsset.Edited -= OnWheelEdited;
            VRSLGoboWheelAsset.Edited += OnWheelEdited;
#endif
        }

#if UNITY_EDITOR
        static void OnWheelEdited(VRSLGoboWheelAsset wheel) => s_dirty = true;
#endif
    }
}
