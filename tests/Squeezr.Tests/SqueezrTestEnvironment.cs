using System.Runtime.CompilerServices;

namespace Squeezr.Tests;

public static class SqueezrTestEnvironment
{
    // Assembly-wide isolation, applied before the first test touches anything.
    // The central store goes to a per-run temp directory so tests never reach the
    // developer's real ~/.squeezr; an externally set SQUEEZR_HOME wins, which is how
    // a CI job or a debugging session pins it. SQUEEZR_CLIENT_HOME does the same for
    // the agent-client profile - without it, Windows tests wrote MCP entries into the
    // real ~/.claude.json, because GetFolderPath(UserProfile) ignores the environment
    // there. HOME covers the same resolution on Unix.
    // CLAUDE_PROJECT_DIR is cleared so the resolver's environment fallback cannot leak
    // an outer session's project into tests that run outside a workspace; the
    // env-fallback suite sets the variable explicitly where it is the subject.
    [ModuleInitializer]
    public static void RedirectSqueezrHome()
    {
        var staleCutoff = TimeSpan.FromHours(6);
        FixtureSweep.SweepStaleRoots(TestDirs.FixtureBaseRoot(), staleCutoff, DateTime.UtcNow);
        FixtureSweep.SweepStaleRoots(TestDirs.LegacyPruneBaseRoot(), staleCutoff, DateTime.UtcNow);

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SQUEEZR_HOME")))
        {
            Environment.SetEnvironmentVariable("SQUEEZR_HOME", TestDirs.SharedStoreDir);
        }

        Environment.SetEnvironmentVariable("SQUEEZR_CLIENT_HOME", TestDirs.SharedClientHomeDir);
        Environment.SetEnvironmentVariable("HOME", TestDirs.SharedClientHomeDir);
        Environment.SetEnvironmentVariable("CLAUDE_PROJECT_DIR", null);
    }
}
