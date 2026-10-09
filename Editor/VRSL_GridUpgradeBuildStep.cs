using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.SceneManagement;

namespace VRSL.URP.EditorScripts
{
    /// <summary>
    /// Brings rc.6 DMX managers across in the built copy of each scene, so a scene
    /// nobody has opened since still builds reading the right grid. The scene on disk is
    /// left alone; opening and saving it once makes the change for good.
    /// </summary>
    /// <remarks>
    /// Unity runs it for every scene going into a player and for scenes built into
    /// AssetBundles, which is how Basis ships worlds; the bundle carries the processed
    /// copy. Nothing runs when a scene is loaded, from a bundle or otherwise, so a
    /// bundle built before this existed keeps whatever it was built with.
    /// </remarks>
    class VRSL_GridUpgradeBuildStep : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report) =>
            VRSLGridUpgrade.UpgradeScene(scene);
    }
}
