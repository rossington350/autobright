using System.Runtime.CompilerServices;

// Exposes internal (non-public) members — the pure mapping/smoothing algorithms
// in BrightnessController — to the unit test project.
[assembly: InternalsVisibleTo("AutoBright.Tests")]
