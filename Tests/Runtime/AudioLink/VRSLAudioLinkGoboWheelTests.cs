#if UNITY_EDITOR
using NUnit.Framework;

namespace VRSL.URP.Tests
{
    /// <summary>GW6 and GW7 of TESTING.md on the AudioLink manager, which Club Orion's
    /// importer writes its logo into as well.</summary>
    class VRSLAudioLinkGoboWheelTests : VRSLDMXTest
    {
        const string Prefab =
            "Packages/town.mr.vrsl-urp/Runtime/Prefabs/AudioLink/VRSL-AudioLink-LightManager.prefab";

        [Test]
        public void GW6_a_saved_AudioLink_manager_with_its_own_list_keeps_it()
            => VRSLGoboWheelTests.KeepsItsOwnList(Prefab);

        [Test]
        public void GW7_a_saved_AudioLink_manager_with_upstreams_eight_takes_the_default_wheel()
            => VRSLGoboWheelTests.TakesTheDefault(Prefab);
    }
}
#endif
