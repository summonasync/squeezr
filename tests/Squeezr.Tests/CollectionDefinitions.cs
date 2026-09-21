namespace Squeezr.Tests;

// One serialized collection for every test that mutates process-global state:
// the current directory, PATH, SQUEEZR_HOME, SQUEEZR_CLIENT_HOME, HOME, or the
// spawning of CLI processes that read them. Two collections used to split this
// set, which meant they ran in parallel with each other and raced on exactly the
// state they were serializing internally.
[CollectionDefinition("ProcessGlobals", DisableParallelization = true)]
public sealed class ProcessGlobalsCollection;
