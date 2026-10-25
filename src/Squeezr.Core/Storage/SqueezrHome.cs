namespace Squeezr.Core.Storage;

public static class SqueezrHome
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private static readonly object Sync = new();
    private static string? _resolved;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Resolve()
    {
        lock (Sync)
        {
            if (_resolved is null)
            {
                var env = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
                _resolved = !string.IsNullOrWhiteSpace(env) ? env : DefaultHome();
            }

            return _resolved;
        }
    }

    public static string DefaultHome() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), SqueezrPaths.StoreDirName);

    public static string WorkspacesDir => Path.Combine(Resolve(), SqueezrPaths.WorkspacesDirName);

    public static bool IsCentralStore(string directory)
    {
        var canonical = WorkspaceCanonicalPath.ResolveThroughLinks(directory);
        return string.Equals(canonical, WorkspaceCanonicalPath.ResolveThroughLinks(DefaultHome()), PathComparison) ||
               string.Equals(canonical, WorkspaceCanonicalPath.ResolveThroughLinks(Resolve()), PathComparison);
    }

    public static bool IsDefaultHome(string directory)
    {
        return string.Equals(
            WorkspaceCanonicalPath.ResolveThroughLinks(directory),
            WorkspaceCanonicalPath.ResolveThroughLinks(DefaultHome()),
            PathComparison);
    }

    public static void Reset()
    {
        lock (Sync)
        {
            _resolved = null;
        }
    }
}
