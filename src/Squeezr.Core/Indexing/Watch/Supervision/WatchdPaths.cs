using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Watch.Supervision;

public static class WatchdPaths
{
    public const string DirName = "watchd";
    public const string LockFileName = "watchd.lock";

    public static string Dir => Path.Combine(SqueezrHome.Resolve(), DirName);

    public static string StatePath => Path.Combine(Dir, SqueezrPaths.StateFileName);

    public static string LockPath => Path.Combine(Dir, LockFileName);
}
