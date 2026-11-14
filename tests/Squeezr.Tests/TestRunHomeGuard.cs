using Squeezr.Core.Storage;

namespace Squeezr.Tests;

// REQ-1 of feature 118: a test run's central store may never be a profile home. This state is reached by
// accident rather than by intent — a Dispose that clears SQUEEZR_HOME from the whole host process — so the
// remedy travels in the message and says which of the two holes the reader is looking at.
internal static class TestRunHomeGuard
{
    public const string Remedy =
        "pin SQUEEZR_HOME to a scratch store (SqueezrTestEnvironment does it at module load) or run the " +
        "suite from a root outside $HOME; a pin that is missing here was cleared in-process by a test";

    // requireAmbientPin is only safe where the environment still belongs to the run itself, i.e. at
    // module load. Checking it at every child spawn would race the serialized classes that blank the
    // variable on purpose to prove the product's fallback, and would blame them for a spawn they never made.
    public static string RequireContained(string home, bool requireAmbientPin)
    {
        var reason = Uncontained(home, requireAmbientPin);
        return reason is null
            ? home
            : throw new InvalidOperationException(
                $"the test run store is not contained: {reason} (home = {home}). {Remedy}");
    }

    private static string? Uncontained(string home, bool requireAmbientPin)
    {
        if (home.Length == 0)
            return "it resolved to nothing";

        if (SqueezrHome.IsDefaultHome(home))
            return "it is the profile default home";

        if (requireAmbientPin &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable)))
            return "the process holds no SQUEEZR_HOME pin";

        return null;
    }
}
