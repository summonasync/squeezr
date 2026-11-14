using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public static class SqueezrHome
{
    public const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private static readonly object Sync = new();
    private static string? _resolved;
    private static bool _resolvedFromDefault;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // The cache remembers which of the two sources produced the value, because they are not equally
    // durable. A value read from an explicit pin stays frozen for the process: that is what a long-lived
    // host relies on when it sets the variable once at startup and something later clears it around a
    // child spawn. A value computed from the profile fallback carries no such intent — it is only what the
    // answer was while the environment said nothing — so an explicit pin that appears later wins over it.
    // Without the distinction, a process that blanks the variable and re-pins it keeps writing into
    // whichever home the blank window happened to name; 118 measured that as 17 registrations landing in
    // a developer's real store from code that never asked for it.
    public static string Resolve()
    {
        lock (Sync)
        {
            var env = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
            var pinned = !string.IsNullOrWhiteSpace(env);
            if (_resolved is null || (_resolvedFromDefault && pinned))
            {
                _resolvedFromDefault = !pinned;
                _resolved = pinned ? env! : DefaultHome();
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
    // world. SQUEEZR_HOME is the user's explicit choice and never trips this. No profile at all
    // (HOME unset and no passwd entry) refuses the same way — pre-fix that corner silently
    // anchored the central store to the process working directory.
    public static void RequireHabitableProfileHome()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HomeEnvironmentVariable)))
        {
            return;
        }

        if (UserHome.Path is not { } home)
        {
            throw new WorkspaceException(
                $"no user home directory could be resolved — set {HomeEnvironmentVariable} to a central store or fix HOME",
                WorkspaceException.HomeUnavailableCode);
        }

        if (!Directory.Exists(home))
        {
            throw new WorkspaceException(
                $"the user home directory '{home}' is not a usable directory — HOME dangles; set {HomeEnvironmentVariable} to a central store or fix HOME",
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
            _resolvedFromDefault = false;
        }
    }
}
