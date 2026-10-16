// Editor-only, and in the runtime assembly beside VRSLWiring for the same reason: the
// migration tool compiles only when the legacy package is installed, and the suite has to
// read this table in any project.
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRSL.URP
{
    enum VRSLMigrationKind { AudioLink, Dmx }

    /// <summary>An upstream fixture prefab and the URP prefab that replaces it.</summary>
    readonly struct VRSLMigrationPair
    {
        public readonly string UpstreamGuid;
        public readonly string UrpGuid;
        public readonly string FriendlyName;
        public readonly VRSLMigrationKind Kind;
        /// <summary>Copy the source's local scale onto the sibling. Off for a source
        /// whose root scale is a projection volume rather than the fixture.</summary>
        public readonly bool MatchScale;
        /// <summary>Where the sibling's root goes, in the source root's space: turned by
        /// <see cref="RootTurn"/> (Euler degrees, whole quarter turns) and moved to
        /// <see cref="RootShift"/>. Zero for a URP prefab that carries its body as the upstream
        /// one does.</summary>
        public readonly Vector3 RootTurn;
        public readonly Vector3 RootShift;

        public VRSLMigrationPair(string upstream, string urp, string friendly, VRSLMigrationKind kind,
            bool matchScale = true, Vector3 rootTurn = default, Vector3 rootShift = default)
        {
            UpstreamGuid = upstream;
            UrpGuid = urp;
            FriendlyName = friendly;
            Kind = kind;
            MatchScale = matchScale;
            RootTurn = rootTurn;
            RootShift = rootShift;
        }
    }

    /// <summary>
    /// Which upstream <c>com.acchosen.vr-stage-lighting</c> fixture prefabs the migration
    /// tool recognises, by GUID.
    /// </summary>
    /// <remarks>
    /// A fixture prefab here serves every grid layout, because the layout is the manager's.
    /// So Horizontal, Legacy and Vertical upstream fixtures pair with the same URP prefab.
    /// Upstream shipped its Legacy fixtures twice: under <c>Prefabs/DMX/Legacy Mode/</c>,
    /// and from v2.1 to v2.3.0 also under <c>Shaders/Basic Surface Shaders/Legacy Mode/</c>
    /// with other GUIDs. Worlds built on those releases can carry either set.
    /// </remarks>
    static class VRSLMigrationTable
    {
        const VRSLMigrationKind AudioLink = VRSLMigrationKind.AudioLink;
        const VRSLMigrationKind Dmx = VRSLMigrationKind.Dmx;

        const string Spotlight13 = "d3e39e2f24901667db5024c000b520cc";
        const string WashLight13 = "b3dde321f5bf01b7f34b56b4f022e9e3";
        const string Blinder13   = "0c48d81deda9b2b42e666ee404a350e5";
        const string ParLight13  = "51451bcea7766727ec63db8b7aebe996";
        const string Blinder5    = "4bd6fe160f434eaa8ae0ad2b7e28d203";
        const string ParLight5   = "e0009838b6fe1c3137b25f4e3658ae65";
        const string Laser13     = "9c1eb5ab51addbc7b6095123da60df6a";
        const string Flasher1    = "5404970a7e74686098d7d8dfef0597f2";
        const string LightBar13  = "2e796cb68041073f45927683e29e808f";
        const string MultiBar15  = "cc5c662de3ca6d24fc0081cbf207613c";
        const string Strobe6x4   = "6ba550857dbb8b35cdd51d4d4a127435";
        const string Discoball   = "3c1f6d2a8e5b4f7a9d0c1b2e3f4a5b6c";

        const string ExampleSurface5 = "91cec4a5ecd89a3b271d226cf9bc78f4";

        public static readonly VRSLMigrationPair[] Pairs =
        {
            new VRSLMigrationPair("2aa50be2d32099842af2903a918a56f7", "17c6ee43b8af20aead4a4ccc0c57c3cb", "AudioLink Mover Spotlight",  AudioLink),
            new VRSLMigrationPair("dd0fe316ce2ca824ead0901561087fd3", "9aa51ab9f0738edf41d2ab84a38c2784", "AudioLink Mover Washlight",  AudioLink),
            // Upstream's AudioLink par and blinder carry their body at the root, shining down; ours
            // carry it turned to shine along the root's forward, the blinder's also moved.
            new VRSLMigrationPair("269647a339f4d1c47951638c83aa839b", "2156195184d840df408a82f7ec31ecd9", "AudioLink Static Blinder",   AudioLink,
                rootTurn: new Vector3(90f, 0f, 0f), rootShift: new Vector3(0f, -0.214f, 0.214f)),
            new VRSLMigrationPair("161d81f8a11b22d42ae4e81f522939d3", "3e6759ca4cb0a19bc3f20487b6c9f2e1", "AudioLink Static ParLight",  AudioLink,
                rootTurn: new Vector3(90f, 0f, 0f)),

            new VRSLMigrationPair("f5be3cfe3f15bfb4e9477904c5af9daf", Spotlight13, "DMX H 13CH Mover Spotlight", Dmx),
            new VRSLMigrationPair("b3e8ff051cc2d684aa255ceccce9b96f", WashLight13, "DMX H 13CH Mover Washlight", Dmx),
            new VRSLMigrationPair("e9dde3e86ccb8ca4bb4ecbe35a6fa7b1", Blinder13,   "DMX H 13CH Static Blinder",  Dmx),
            new VRSLMigrationPair("946b3c09cfa93244c90a4b0ac7764b44", ParLight13,  "DMX H 13CH Static ParLight", Dmx),

            new VRSLMigrationPair("d9cab657bd2dff14ea5425c2c4c4679e", Spotlight13, "DMX L 13CH Mover Spotlight", Dmx),
            new VRSLMigrationPair("41c8453c8957aec4292212174d351a36", WashLight13, "DMX L 13CH Mover Washlight", Dmx),
            new VRSLMigrationPair("9310469001d6cdf4db2145f9fddd7933", Blinder13,   "DMX L 13CH Static Blinder",  Dmx),
            new VRSLMigrationPair("dd7cad5fc7f12624ea58efde5c3cd633", ParLight13,  "DMX L 13CH Static ParLight", Dmx),

            // Upstream's second Legacy set (Shaders/Basic Surface Shaders/Legacy Mode/, v2.1 to v2.3.0).
            new VRSLMigrationPair("26f28624a9a01c54aa5f3fd72dd5cdfa", Spotlight13, "DMX L 13CH Mover Spotlight", Dmx),
            new VRSLMigrationPair("325e4375b1f7510419dc1764fd1fbc48", WashLight13, "DMX L 13CH Mover Washlight", Dmx),
            new VRSLMigrationPair("f478cf1861bc178479b9ed5d8633d9af", Blinder13,   "DMX L 13CH Static Blinder",  Dmx),
            new VRSLMigrationPair("a6e7cc8f0761d7c498225c8298d3a8b3", ParLight13,  "DMX L 13CH Static ParLight", Dmx),
            new VRSLMigrationPair("25ac73327cc0fbe4f802fde982ab43b7", Discoball,   "DMX L 1CH Discoball",        Dmx, matchScale: false),
            new VRSLMigrationPair("36a79d2368f8be441bb6fa73c3df4816", Flasher1,    "DMX L 1CH Static Flasher",   Dmx),
            new VRSLMigrationPair("a82e1cba136b3ff4792a5cebdd895bf6", Laser13,     "DMX L 13CH Static Laser",    Dmx),
            new VRSLMigrationPair("204242a5763407b4c8404bcaa55a99bf", LightBar13,  "DMX L 13CH Static LightBar", Dmx),

            new VRSLMigrationPair("9a6d4144bda0d3c4ba95593af446b653", Spotlight13, "DMX V 13CH Mover Spotlight", Dmx),
            new VRSLMigrationPair("88bee1a0ddf090d4bb0721b30240c949", WashLight13, "DMX V 13CH Mover Washlight", Dmx),
            new VRSLMigrationPair("d7a8bacd5310e8e499962549ef931c57", Blinder13,   "DMX V 13CH Static Blinder",  Dmx),
            new VRSLMigrationPair("2ff8eb277ef9d7047b12d127b2eaeb36", ParLight13,  "DMX V 13CH Static ParLight", Dmx),

            new VRSLMigrationPair("5ae312c8e69488842994fd62a7609adc", Blinder5,  "DMX H 5CH Static Blinder",  Dmx),
            new VRSLMigrationPair("6a94fea4f85300a44b9e29ba54430110", ParLight5, "DMX H 5CH Static ParLight", Dmx),
            new VRSLMigrationPair("94d6ff221dc5748458941750e422114f", Blinder5,  "DMX V 5CH Static Blinder",  Dmx),
            new VRSLMigrationPair("3b7bdfab2bd7abf4295be3356f6f3617", ParLight5, "DMX V 5CH Static ParLight", Dmx),
            new VRSLMigrationPair("fbb24c41d42d23c4296c31f5aca73942", "345a06b27d787f46fdcdf223175c83ef", "DMX H 5CH Static LightBar", Dmx),

            // Shader-driven fixtures: the URP prefab keeps the same Static (or Laser) component
            // as upstream, and the fields copy across by name.
            new VRSLMigrationPair("75c269de381facb4cae616c67f83f519", "4a8f7532473a5e9bf1361da1fe4b77a4", "AudioLink Basic Laser",       AudioLink),
            // The discoballs become realtime lights; upstream's root is a 25 m projection
            // volume, so its scale is not carried over.
            new VRSLMigrationPair("a7acda2f5fe7dfd4aaa49ec10a2d5586", "5e2a7c4b9d1f4e8a8b3c2d1e0f9a8b7c", "AudioLink Discoball",         AudioLink, matchScale: false),
            new VRSLMigrationPair("092158b73b160384f904e33d35a09123", "9e7791cf3563706421af503421e7aa61", "AudioLink Static Flasher",    AudioLink),
            new VRSLMigrationPair("c33f8d4d996a9ba47b86d420e4cdb05b", "7d6e97747390e7734fb4ae838b5f9667", "AudioLink Static Lightbar",   AudioLink),

            new VRSLMigrationPair("3d6c0b40980bcd34aba9183a62ecbd21", Laser13,    "DMX H 13CH Static Laser",         Dmx),
            new VRSLMigrationPair("22192b7ad03f22a4db578b035fdca38d", Discoball,  "DMX H 1CH Discoball",             Dmx, matchScale: false),
            new VRSLMigrationPair("6d00d693f1608ab49ad08d18dbe1fa02", Flasher1,   "DMX H 1CH Static Flasher",        Dmx),
            new VRSLMigrationPair("96ffbd2a722ae324e892d303e2ee9a2a", LightBar13, "DMX H 13CH Static LightBar",      Dmx),
            new VRSLMigrationPair("c19e8fd46b4abdf49bb7b6cdc62acdde", MultiBar15, "DMX H 15CH Static MultiLightBar", Dmx),
            new VRSLMigrationPair("46d1954298362974887b80dc3d70ee5f", Strobe6x4,  "DMX H 5CH Static 6x4 Strobe",     Dmx),

            new VRSLMigrationPair("65a96b17618e51548a669749173d48ff", Discoball,  "DMX L 1CH Discoball",             Dmx, matchScale: false),
            new VRSLMigrationPair("a38b2f56984259247bded9aa1b1ee149", Flasher1,   "DMX L 1CH Static Flasher",        Dmx),
            new VRSLMigrationPair("55ac9bf95dc63bb4fb6ba2095d73cde2", Laser13,    "DMX L 13CH Static Laser",         Dmx),
            new VRSLMigrationPair("b1b81594f59ca5d469ad06808051c682", LightBar13, "DMX L 13CH Static LightBar",      Dmx),
            new VRSLMigrationPair("dd68d30b9f0b34442aac2fb4540ae553", MultiBar15, "DMX L 15CH Static MultiLightBar", Dmx),
            new VRSLMigrationPair("6f5c5d0af7e69e242ad56ac13882e04e", Strobe6x4,  "DMX L 5CH Static 6x4 Strobe",     Dmx),

            new VRSLMigrationPair("55058c5ef8c22d04991b48a99a10acfe", Laser13,    "DMX V 13CH Static Laser",         Dmx),
            new VRSLMigrationPair("8bb1407f1f0e2cc48b9bbf35ca1951a6", Discoball,  "DMX V 1CH Discoball",             Dmx, matchScale: false),
            new VRSLMigrationPair("1c08f57da0cd0414c85f64b373431921", Flasher1,   "DMX V 1CH Static Flasher",        Dmx),
            new VRSLMigrationPair("b2a0b640363bc10408fb7a3803939fa0", LightBar13, "DMX V 13CH Static LightBar",      Dmx),
            new VRSLMigrationPair("b8873da88b401dd4ab93b061c5ddf750", MultiBar15, "DMX V 15CH Static MultiLightBar", Dmx),
            new VRSLMigrationPair("51b428740444288448e19c88c64d5311", Strobe6x4,  "DMX V 5CH Static 6x4 Strobe",     Dmx),
            new VRSLMigrationPair("6b906c7cc969ac346a656210549c02c7", ExampleSurface5, "DMX V 5CH Example Surface", Dmx),
            new VRSLMigrationPair("8c5b2abb4f158154cad77f35b9ce2643", ExampleSurface5, "DMX H 5CH Example Surface", Dmx),
            new VRSLMigrationPair("27dc7840f76fbc1469f1e64bdbc9991a", ExampleSurface5, "DMX L 5CH Example Surface", Dmx),
            new VRSLMigrationPair("f5ed30f5e443da045ad632a1ccf18ff9", ExampleSurface5, "DMX L 5CH Example Surface", Dmx),
        };

        /// <summary>Upstream fixture prefabs this package ships no twin for. Named in the
        /// summary so an author knows which instances were passed over, rather than finding
        /// out in the headset.</summary>
        public static readonly Dictionary<string, string> Unported = new Dictionary<string, string>
        {
            { "78bf4380452cfbe4aa8154e17a189b28", "DMX V 5CH Static LightBar" },
        };

        /// <summary>Upstream's light stencil assets: its mask prefabs, their two materials and
        /// the two shaders, by GUID, each with the name the summary shows. This package has no
        /// light stencil, so the tool names these in a scene rather than migrating them.</summary>
        public static readonly Dictionary<string, string> LightStencils = new Dictionary<string, string>
        {
            { "29158ac9808b32541ba787435bdfa109", "VRSL-StencilMask-Capsule (prefab)" },
            { "14fd9d71e91903d4eb44d5289d78eba4", "VRSL-StencilMask-Cube (prefab)" },
            { "de5b252ed1e43b14a9f655ac98144fff", "VRSL-StencilMask-Cylinder (prefab)" },
            { "fa0972d9c823bcf4293febe61ff79da6", "VRSL-StencilMask-Sphere (prefab)" },
            { "92edb46363dfd9149a96fcecb5b6d192", "VRSL-LightStencil (material)" },
            { "c813d92472af4434ca4f1a05d4703ae3", "VRSL-LightRestorationStencil (material)" },
            { "e17c4816fe62ef5448f6c0cfbdb8e08b", "A material of your own on VRSL/Stencils/VRSL-Light Stencil" },
            { "c61102b627802b54583713cccdc095b8", "A material of your own on VRSL/Stencils/VRSL-Light Restoration Stencil" },
        };

        /// <summary>The summary's paragraph on the light stencils found, by name and count;
        /// empty when there are none.</summary>
        public static string LightStencilSummary(IReadOnlyDictionary<string, int> found)
        {
            if (found == null || found.Count == 0) return "";
            return "Light stencils from the original VRSL, left as they are. VRSL-URP has no light stencil, so "
                 + "they no longer keep light off anything and you can delete them:\n"
                 + string.Join("\n", found.OrderBy(f => f.Key).Select(f => $"  {f.Key}: {f.Value}"))
                 + "\n\nTo keep fixture light off an area, aim the fixtures away from it, narrow their cone or "
                 + "shorten their range. A fixture still lights a surface on the far side of a wall, because "
                 + "the lights have no shadow maps. Beams stop at whatever stands in front of them, and flares and "
                 + "lasers hide behind it.";
        }
    }
}
#endif
