using System.Runtime.CompilerServices;

// VRSL_EditorHeader and friends stay internal to the package. The Basis and
// AudioLink integrations' inspectors are separate assemblies only because they
// are constrained on a package that may not be installed; they draw the same
// header as every other VRSL inspector, and the AudioLink manager's inspector is
// the shared manager inspector.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Basis.Editor")]
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.AudioLink.Editor")]
