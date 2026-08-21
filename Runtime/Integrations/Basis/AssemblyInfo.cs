using System.Runtime.CompilerServices;

// The integration's own inspectors and setup commands are a separate assembly
// only because they are editor-only. Constants they share with the components
// stay internal rather than being widened for them.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Basis.Editor")]
