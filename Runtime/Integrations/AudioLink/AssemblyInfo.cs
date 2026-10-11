using System.Runtime.CompilerServices;

// The integration's inspectors and setup command are a separate assembly only
// because they are editor-only, and the rows reach the wiring table the same way.
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.AudioLink.Editor")]
[assembly: InternalsVisibleTo("Towneh.VRSL.URP.AudioLink.Tests")]
