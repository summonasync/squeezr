namespace Squeezr.Core.Storage;

public static class SqueezrHome
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private static readonly object Sync = new();
    private static string? _resolved;

    public static string Resolve()
    {
        lock (Sync)
        {
            if (_resolved is null)
            {
                var env = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
                _resolved = !string.IsNullOrWhiteSpace(env)
                    ? env
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".squeezr");
            }

            return _resolved;
        }
    }

    public static string WorkspacesDir => Path.Combine(Resolve(), SqueezrPaths.WorkspacesDirName);

    public static bool IsCentralStore(string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            WorkspaceCanonicalPath.ResolveThroughLinks(directory),
            WorkspaceCanonicalPath.ResolveThroughLinks(Resolve()),
            comparison);
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _resolved = null;
        }
    }
}
