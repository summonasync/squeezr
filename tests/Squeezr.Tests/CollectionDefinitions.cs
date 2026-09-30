namespace Squeezr.Tests;

// SqueezrHomeScope was merged into CliProcessGlobals: both collections touch
// process-wide state (SQUEEZR_HOME env var + SqueezrHome._resolved static cache)
// and must not run in parallel with CLI-spawning tests that inherit the same state.
[CollectionDefinition("CliProcessGlobals", DisableParallelization = true)]
public sealed class CliProcessGlobalsCollection;
