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

    public static string RequireContainedAtLoad(string home)
    {
        if (home.Length == 0)
            return throw new InvalidOperationException(
                $"the test run store resolved to nothing. {Remedy}");

        if (SqueezrHome.IsDefaultHome(home))
            return throw new InvalidOperationException(
                $"the test run store is the profile default home '{home}'. {Remedy}");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable)))
            return throw new InvalidOperationException(
                $"the test run has no {SqueezrHome.HomeEnvironmentVariable} pin while its store answers " +
                $"'{home}'. {Remedy}");

        return home;
    }

    // capturedFromPin is read in the same breath as the value itself — see CliTestProcess.RunStore — so
    // this check never consults the environment again and cannot be moved by a neighbour.
    public static string RequirePinnedSnapshot(string home, bool capturedFromPin) =>
        capturedFromPin
            ? home
            : throw new InvalidOperationException(
                $"the test run store '{home}' was frozen from the profile fallback, not from an explicit " +
                $"{SqueezrHome.HomeEnvironmentVariable}. {Remedy}");
}
