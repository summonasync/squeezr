namespace Squeezr.Tests;

// SqueezrHomeScope was merged into CliProcessGlobals: both collections touch
// process-wide state (SQUEEZR_HOME env var + SqueezrHome._resolved static cache)
// and must not run in parallel with CLI-spawning tests that inherit the same state.
[CollectionDefinition("CliProcessGlobals", DisableParallelization = true)]
public sealed class CliProcessGlobalsCollection;

// RoslynSession.Create registers MSBuildLocator process-wide and the MSBuild fixture tests shell real
// restores: the same risk class as the CLI globals above, contoured as its own collection (055/T8).
[CollectionDefinition("MsBuildLocator", DisableParallelization = true)]
public sealed class MsBuildLocatorCollection;
