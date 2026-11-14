using System.Runtime.CompilerServices;
using Squeezr.Core.Storage;

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
        // Before anything touches the environment: the profile store this process actually started with.
        // The guard needs it because after HOME is redirected below, "is this a profile home" can only be
        // answered against the sandbox, and a run that inherited SQUEEZR_HOME=/home/<dev>/.squeezr would
        // pass every arm while writing fixture rows into that store (review-118-05-t2, REQ-1).
        TestRunHomeGuard.ProfileStoreAtStartup = SqueezrHome.DefaultHome();

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

        // Both branches above land here, and HOME is now the run's own, so this is the one moment where
        // the ambient pin describes the run instead of whichever test last held the serialized collection.
        // REQ-1 (118): an inherited pin that points at a profile home is refused at module load, not
        // discovered later as leaked rows in the developer's registry. What survives the check is recorded
        // as this run's identity: the only value no test can have influenced yet.
        TestRunHomeGuard.RunStoreAtModuleLoad = TestRunHomeGuard.RequireContainedAtLoad(SqueezrHome.Resolve());

        // The child-store snapshot is taken here, in the same instant, so no test can be the first to
        // decide it; see CliTestProcess.PrimeRunStore.
        CliTestProcess.PrimeRunStore();
    }
}
