using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Watch.Supervision;

public static class WatchdPaths
{
    public const string DirName = "watchd";
    public const string LockFileName = "watchd.lock";

    public static string Dir => Path.Combine(SqueezrHome.Resolve(), DirName);

    public static string StatePath => Path.Combine(Dir, SqueezrPaths.StateFileName);

    public static string LockPath => Path.Combine(Dir, LockFileName);

    // The unit carries SQUEEZR_HOME whenever install was told one explicitly, so the file the instance
    // it starts will open is the one under that home, not under whatever this shell happens to resolve.
    public static string LockPathUnder(string? squeezrHome) =>
        string.IsNullOrWhiteSpace(squeezrHome)
            ? LockPath
            : Path.Combine(squeezrHome, DirName, LockFileName);
}
