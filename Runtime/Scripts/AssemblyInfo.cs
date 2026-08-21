using System.Runtime.CompilerServices;

// The DMX rows read the decoded light data back off the GPU and compare it with
// what the patch predicts. That data is VRSLLightData, which stays internal
// because its layout is a contract with the compute shader rather than public
// API — exposing it to make it testable would invite a consumer to depend on it.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Tests")]
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Basis.Tests")]
