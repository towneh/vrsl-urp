using System.Runtime.CompilerServices;

// The Basis and AudioLink integration rows drive the same rig as the rest of the
// suite, from assemblies that only exist when those packages do.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Basis.Tests")]
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.AudioLink.Tests")]
