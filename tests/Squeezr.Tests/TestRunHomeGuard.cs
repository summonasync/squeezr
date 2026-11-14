using Squeezr.Core.Storage;

namespace Squeezr.Tests;

// REQ-1 of feature 118: a test run's central store may never be a profile home. This state is reached by
// accident rather than by intent — a Dispose that clears SQUEEZR_HOME from the whole host process — so the
// remedy travels in the message and says which of the two holes the reader is looking at.
//
// Two entry points, because the two moments know different things. At module load the environment still
// describes the run, so the value itself can be judged: is it a profile home, is the pin there at all.
// At child-spawn time a parallel fact may have moved HOME or blanked the pin a second ago, and comparing
// the run's snapshot against whatever DefaultHome() answers NOW is both a race and a false negative — the
// poisoned snapshot stays poisoned when a neighbour has meanwhile restored HOME, which is exactly the
// state a real suite reaches. So the spawn-time question is not "where does this value point today" but
// "how was it obtained": a store the run froze from an explicit pin is trusted, a store it froze from the
// profile fallback is refused on sight, whichever directory the ambient environment happens to name.
internal static class TestRunHomeGuard
{
    public const string Remedy =
        "pin SQUEEZR_HOME to a scratch store (SqueezrTestEnvironment does it at module load) or run the " +
        "suite from a root outside $HOME; a pin that is missing here was cleared in-process by a test";

    // The developer's profile store as the process found it, captured before the harness redirects HOME.
    // Without it the only "is this a profile home" comparison the guard can make is against the SANDBOX
    // profile, which the initializer created on purpose - so a run that inherited
    // SQUEEZR_HOME=/home/<dev>/.squeezr answered every arm and went ahead writing fixture rows into the
    // real registry, which is REQ-1's hard-error case (found by review-118-05-t2).
    public static string? ProfileStoreAtStartup { get; set; }

    public static string RequireContainedAtLoad(string home)
    {
        if (home.Length == 0)
        {
            throw new InvalidOperationException(
                $"the test run store resolved to nothing. {Remedy}");
        }

        if (ProfileStoreAtStartup is { Length: > 0 } startup &&
            SamePath(home, startup))
        {
            throw new InvalidOperationException(
                $"the test run store is the profile home the process started with ('{home}'). {Remedy}");
        }

        if (SqueezrHome.IsDefaultHome(home))
        {
            throw new InvalidOperationException(
                $"the test run store is the profile default home '{home}'. {Remedy}");
        }

        return home;
    }

    // The store this run claimed at module load, recorded by SqueezrTestEnvironment where the environment
    // still describes the run and nothing else has pinned anything yet. Provenance and shape cannot answer
    // the question that remains: a ProcessGlobals fact pins SQUEEZR_HOME to its own scratch dir for the
    // width of its own test, and if the run's first CliTestProcess touch lands in that window, the captured
    // pair is (somebody else's dir, FromPin=true) - explicit, non-profile, present. Every arm passes and the
    // whole run then spawns children against a directory its owner deletes in Dispose. Only a value taken
    // before any test could interfere can say whether a store is THIS run's.
    public static string? RunStoreAtModuleLoad { get; set; }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            WorkspaceCanonicalPath.ResolveThroughLinks(left),
            WorkspaceCanonicalPath.ResolveThroughLinks(right),
            StringComparison.Ordinal);

    // Provenance comes from the product's own cache (SqueezrHome.ResolveWithProvenance), so this check never
    // consults the environment again and cannot be moved by a neighbour between two reads.
    public static string RequirePinnedSnapshot(string home, bool capturedFromPin)
    {
        if (!capturedFromPin)
        {
            throw new InvalidOperationException(
                $"the test run store '{home}' was frozen from the profile fallback, not from an explicit " +
                $"{SqueezrHome.HomeEnvironmentVariable}. {Remedy}");
        }

        if (RunStoreAtModuleLoad is { Length: > 0 } claimed && !SamePath(home, claimed))
        {
            throw new InvalidOperationException(
                $"the test run store '{home}' is not the store this run claimed at module load ('{claimed}') " +
                $"— a neighbouring test's {SqueezrHome.HomeEnvironmentVariable} was live when the snapshot " +
                $"was taken. {Remedy}");
        }

        return home;
    }
}
