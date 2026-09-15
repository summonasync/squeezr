using System.Runtime.CompilerServices;

namespace Squeezr.Tests;

public static class SqueezrTestEnvironment
{
    // Redirects the central store into a per-run temp directory so tests never
    // touch the developer's real ~/.squeezr. An externally set SQUEEZR_HOME wins.
    // CLAUDE_PROJECT_DIR is cleared so the resolver's environment fallback cannot
    // leak an outer session's project into tests that run outside a workspace;
    // the env-fallback suite sets the variable explicitly where it is the subject.
    [ModuleInitializer]
    public static void RedirectSqueezrHome()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SQUEEZR_HOME")))
        {
            Environment.SetEnvironmentVariable(
                "SQUEEZR_HOME",
                Path.Combine(Path.GetTempPath(), "squeezr-tests-home", Guid.NewGuid().ToString("N")));
        }

        Environment.SetEnvironmentVariable("CLAUDE_PROJECT_DIR", null);
    }
}
