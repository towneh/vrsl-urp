using System.Runtime.CompilerServices;

// The Basis integration rows drive the same rig as the rest of the suite, from
// an assembly that only exists when the Basis packages do.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Basis.Tests")]
