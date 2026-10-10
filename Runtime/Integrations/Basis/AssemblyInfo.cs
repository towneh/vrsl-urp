using System.Runtime.CompilerServices;

// The integration's own inspectors and setup commands are a separate assembly
// only because they are editor-only. Constants they share with the components
// stay internal rather than being widened for them, and the rows reach them the
// same way.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Basis.Editor")]
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.Basis.Tests")]
