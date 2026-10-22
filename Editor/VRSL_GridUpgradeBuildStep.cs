using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.SceneManagement;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Moves DMX managers saved with the five separate grid texture fields onto a Grid
    /// Textures asset in the built copy of each scene, so a scene nobody has opened since
    /// still builds reading the right grid, and gives every light manager there the look
    /// at its gobos it would get on opening (VRSLGoboWheelUpgrade), so one with gobos of
    /// its own builds with them. The scene on disk is left alone; opening and saving it
    /// once makes the change for good.
    /// </summary>
    /// <remarks>
    /// Unity runs it for every scene going into a player and for scenes built into
    /// AssetBundles, which is how Basis ships worlds; the bundle carries the processed
    /// copy. Nothing runs when a scene is loaded, from a bundle or otherwise, so a
    /// bundle already built keeps whatever it was built with.
    /// </remarks>
    class VRSL_GridUpgradeBuildStep : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            VRSLGoboWheelUpgrade.UpgradeScene(scene);
            VRSLGridUpgrade.UpgradeScene(scene);
        }
    }
}
