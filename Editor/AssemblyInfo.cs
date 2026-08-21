using System.Runtime.CompilerServices;

// VRSL_EditorHeader and friends stay internal to the package. The Basis
// integration's inspectors are a separate assembly only because they are
// constrained on a package that may not be installed, and they draw the same
// header as every other VRSL inspector.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Basis.Editor")]
