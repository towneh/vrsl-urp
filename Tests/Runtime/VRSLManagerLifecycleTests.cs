using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRSL.URP.Tests
{
    /// <summary>
    /// Who ends up owning the manager singleton when more than one manager loads.
    ///
    /// Everything that reaches a manager goes through <c>Instance</c> — the render
    /// passes, and any channel source registering itself — so a scene where the wrong
    /// component holds it renders nothing at all and logs nothing to say why. That is
    /// the failure worth a row: it looks like a scene with the lighting turned off
    /// rather than like a fault.
    /// </summary>
    class VRSLManagerLifecycleTests : VRSLDMXTest
    {
        /// <summary>
        /// A manager that starts switched off must leave the singleton alone.
        ///
        /// Awake runs on a disabled component whose GameObject is active, while OnEnable
        /// and OnDisable never do. A claim made in Awake by such a component is therefore
        /// never released, and the manager that is actually running destroys itself as a
        /// duplicate against an owner that does nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator ASwitchedOffManagerLeavesTheSingletonAlone()
        {
            Assert.IsNull(VRSL_URPLightManager.Instance,
                "something outside this row already holds the singleton, so the row cannot "
              + "tell who claimed it");

            GameObject dormantGo = null, ownerGo = null;
            try
            {
                // Built inactive on purpose. AddComponent on an active GameObject runs
                // Awake before `enabled` can be set, which is not the case being modelled —
                // a component serialised into a scene switched off arrives exactly this way.
                dormantGo = new GameObject("dormant manager");
                dormantGo.SetActive(false);
                dormantGo.AddComponent<VRSL_URPLightManager>().enabled = false;
                dormantGo.SetActive(true);

                Assert.IsNull(VRSL_URPLightManager.Instance,
                    "a switched-off manager claimed the singleton in Awake, and it will never "
                  + "release it: OnDisable does not run on a component that was never enabled");

                ownerGo = new GameObject("running manager");
                var owner = ownerGo.AddComponent<VRSL_URPLightManager>();
                yield return null;

                // Order matters here. Destroy is deferred to the end of the frame, so the
                // survival check needs the frame above; and it has to come first, because a
                // destroyed owner compares equal to a null Instance under Unity's operator
                // and the ownership assert below would pass on two nothings.
                Assert.IsTrue(owner != null,
                    "the running manager destroyed itself as a duplicate, so nothing in the "
                  + "scene drives the light path");
                Assert.IsTrue(VRSL_URPLightManager.Instance == owner,
                    "the running manager did not end up owning the singleton");
            }
            finally
            {
                if (ownerGo != null)   Object.DestroyImmediate(ownerGo);
                if (dormantGo != null) Object.DestroyImmediate(dormantGo);
            }

            Assert.IsNull(VRSL_URPLightManager.Instance,
                "the singleton outlived both managers, so every row after this one starts "
              + "against a claim it cannot see");
        }

        /// <summary>
        /// The owner standing down hands the singleton to a manager that is still running.
        ///
        /// A manager enabled while something else owns the singleton stands down, and
        /// nothing re-runs OnEnable on a component that is already enabled. Without a
        /// handover it would stay stood down for the rest of the session, so switching
        /// off the owner would leave the scene with an enabled manager, no owner and no
        /// lighting.
        /// </summary>
        [UnityTest]
        public IEnumerator StandingDownHandsTheSingletonToAManagerThatIsStillRunning()
        {
            Assert.IsNull(VRSL_URPLightManager.Instance,
                "something outside this row already holds the singleton, so the row cannot "
              + "tell who claimed it");

            GameObject ownerGo = null, spareGo = null;
            try
            {
                ownerGo = new GameObject("running manager");
                var owner = ownerGo.AddComponent<VRSL_URPLightManager>();

                // Inactive first so its Awake sees `enabled` already false, which is how a
                // component serialised into a scene switched off arrives.
                spareGo = new GameObject("spare manager");
                spareGo.SetActive(false);
                var spare = spareGo.AddComponent<VRSL_URPLightManager>();
                spare.enabled = false;
                spareGo.SetActive(true);

                spare.enabled = true;
                yield return null;
                Assert.IsTrue(VRSL_URPLightManager.Instance == owner,
                    "the spare took the singleton from the running manager on being enabled");

                owner.enabled = false;
                yield return null;

                Assert.IsTrue(VRSL_URPLightManager.Instance == spare,
                    "the owner stood down and handed the singleton to nobody. The spare is "
                  + "still enabled and will not get another OnEnable, so the scene has a "
                  + "manager in it and no light path");
            }
            finally
            {
                if (ownerGo != null) Object.DestroyImmediate(ownerGo);
                if (spareGo != null) Object.DestroyImmediate(spareGo);
            }

            Assert.IsNull(VRSL_URPLightManager.Instance,
                "the singleton outlived both managers, so every row after this one starts "
              + "against a claim it cannot see");
        }
    }
}
