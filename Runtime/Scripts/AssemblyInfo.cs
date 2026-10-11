using System.Runtime.CompilerServices;

// The DMX rows read the decoded light data back off the GPU and compare it with
// what the patch predicts. That data is VRSLLightData, which stays internal
// because its layout is a contract with the compute shader rather than public
// API — exposing it to make it testable would invite a consumer to depend on it.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Tests")]
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Basis.Tests")]

// The performance window and the sweep runner drive the benchmark harness, whose
// scene builder and quality preset are internal: the preset in particular is a shim
// that M1 replaces with real public API, and a second public way to set quality is
// exactly what should not outlive it.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Editor")]

// The AudioLink integration compiles only where AudioLink is installed. Its manager
// shares the wiring resolver, the light-path list and the interfaces the package's
// tools reach it through, all internal: they are how the package finds its own
// managers, not API an author should build on.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.AudioLink")]
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.AudioLink.Editor")]
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.AudioLink.Tests")]
