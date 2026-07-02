using System.Runtime.CompilerServices;

// The Unity test assembly (Framedash.Tests, see Tests/Framedash.Tests.asmdef)
// compiles as a SEPARATE assembly that references Framedash.Runtime, so it needs
// explicit access to the internal members the tests exercise (currently
// SessionIdGenerator.ClampNonNegativeUnixMs). The engine-free dotnet NUnit
// harness compiles the Runtime sources directly into its own test assembly, so
// this attribute is a no-op there but is required for the tests to compile under
// Unity when a consumer opts the package into "testables".
[assembly: InternalsVisibleTo("Framedash.Tests")]
