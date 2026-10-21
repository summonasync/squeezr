namespace Squeezr.Tests;

// One serialized collection for every test that mutates or depends on process-global state:
// the current directory, PATH, SQUEEZR_HOME, SQUEEZR_CLIENT_HOME, HOME, the spawning of CLI
// processes that read them, and MSBuildLocator registration (its fixture tests shell real
// restores, so they sit in the same race class). Two collections used to split this
// set, which meant they ran in parallel with each other and raced on exactly the
// state they were serializing internally — the Windows line (051) merged them; the
// MsBuildLocator wave (055/T8) folded in here after the merge.
[CollectionDefinition("ProcessGlobals", DisableParallelization = true)]
public sealed class ProcessGlobalsCollection;
