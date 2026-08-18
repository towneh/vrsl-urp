using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Runs first and measures nothing.
    ///
    /// The project these rows run in is a Basis client, and its systems log an error
    /// about a missing scene once they have been ticking for a while. The framework
    /// fails any test that sees an unhandled error log, so that one message failed
    /// whichever row happened to be running when it fired — a different one each run,
    /// which reads as flakiness in the rows rather than as a fixed event in the host.
    ///
    /// <c>LogAssert.ignoreFailingMessages</c> is not enough on its own: the runner
    /// resets it around each yield. Swapping <c>Debug.unityLogger.logHandler</c> does
    /// not catch it either, because the host logs through a logger of its own. So the
    /// message is absorbed here instead, by idling long enough for it to fire before
    /// any row is measuring.
    ///
    /// The class name sorts ahead of the others deliberately; fixtures run in name
    /// order.
    /// </summary>
    class VRSLDMXAWarmUp
    {
        [UnityTest]
        public IEnumerator Let_the_host_project_finish_complaining()
        {
            LogAssert.ignoreFailingMessages = true;
            float was = Time.captureDeltaTime;
            Time.captureDeltaTime = VRSLDMXRig.FrameDelta;
            try
            {
                for (int i = 0; i < 600; i++) yield return null;
            }
            finally { Time.captureDeltaTime = was; }
        }
    }
}
