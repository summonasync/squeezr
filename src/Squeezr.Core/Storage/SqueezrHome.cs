using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public static class SqueezrHome
{
    public const string HomeEnvironmentVariable = "SQUEEZR_HOME";
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

    public static string DefaultHome() => UserHome.Path is { } home
        ? Path.Combine(home, SqueezrPaths.StoreDirName)
        : string.Empty;

    public static string WorkspacesDir => Path.Combine(Resolve(), SqueezrPaths.WorkspacesDirName);

    // A profile-anchored home whose profile directory is gone is the dangling-HOME environment
    // error (BUGS/010): registry commands refuse with a remedy instead of answering an empty
    // world. SQUEEZR_HOME is the user's explicit choice and never trips this.
    public static void RequireHabitableProfileHome()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HomeEnvironmentVariable)))
        {
            return;
        }

        if (UserHome.Path is { } home && !Directory.Exists(home))
        {
            throw new WorkspaceException(
                $"the user home directory '{home}' does not exist — HOME dangles; set {HomeEnvironmentVariable} to a central store or fix HOME",
                WorkspaceException.HomeUnavailableCode);
        }
    }

    public static bool IsCentralStore(string directory)
    {
        var canonical = WorkspaceCanonicalPath.ResolveThroughLinks(directory);
        return Matches(canonical, DefaultHome()) || Matches(canonical, Resolve());
    }

    public static bool IsDefaultHome(string directory) =>
        Matches(WorkspaceCanonicalPath.ResolveThroughLinks(directory), DefaultHome());

    private static bool Matches(string canonical, string other) =>
        other.Length > 0 &&
        string.Equals(canonical, WorkspaceCanonicalPath.ResolveThroughLinks(other), PathComparison);

    // A store dir under some other home (a foreign SQUEEZR_HOME, a stale profile) is shaped
    // exactly like a legacy in-project store; anything carrying a registry or a workspaces
    // dir is a central store and must never be migrated away.
    public static bool LooksLikeCentralStore(string directory) =>
        File.Exists(Path.Combine(directory, SqueezrPaths.RegistryFileName)) ||
        Directory.Exists(Path.Combine(directory, SqueezrPaths.WorkspacesDirName));

    public static void Reset()
    {
        lock (Sync)
        {
            _resolved = null;
        }
    }
}
