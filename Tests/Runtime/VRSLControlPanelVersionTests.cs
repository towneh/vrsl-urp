#if UNITY_EDITOR
using NUnit.Framework;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Row Q10 of TESTING.md: the control panel's inspector names the installed version
    /// wherever the package lives. The expected version is the package manifest's, not
    /// VERSION.txt's, so a label reading a path that misses, or a VERSION.txt left behind
    /// at a release cut, fails.
    /// </summary>
    public class VRSLControlPanelVersionTests
    {
        [Test]
        public void Q10_TheControlPanelInspectorNamesTheInstalledVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(VRSLWiring.PackageRoot);
            Assert.IsNotNull(info, "this package isn't installed as a package here");

            string label = VRSL_LocalUIControlPanel_Editor.GetVersion();
            StringAssert.Contains($">{info.version}<", label,
                $"the label should name {info.version}, installed from {info.resolvedPath}");
        }
    }
}
#endif
