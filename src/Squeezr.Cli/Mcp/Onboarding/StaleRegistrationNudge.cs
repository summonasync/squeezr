namespace Squeezr.Cli.Mcp.Onboarding;

public static class StaleRegistrationNudge
{
    private const string RelativeLinkPath = ".local/bin/squeezr";

    public static void TryWrite(TextWriter stderr, string homeDir, string? processPath)
    {
        if (processPath is null) return;

        try
        {
            var linkPath = Path.Combine(homeDir, RelativeLinkPath);
            if (!File.Exists(linkPath)) return;

            var linkTarget = FinalTarget(linkPath);
            var runningTarget = FinalTarget(processPath);

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (string.Equals(linkTarget, runningTarget, comparison)) return;

            stderr.WriteLine("hint: the registered squeezr command may be stale — run 'squeezr mcp doctor' to diagnose or 'squeezr mcp install' to repair");
        }
        catch
        {
        }
    }

    private static string FinalTarget(string path)
    {
        var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
        if (resolved is not null) return resolved.FullName;
        return Path.GetFullPath(path);
    }
}
