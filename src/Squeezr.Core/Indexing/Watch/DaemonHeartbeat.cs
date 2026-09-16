namespace Squeezr.Core.Indexing.Watch;

public sealed record DaemonHeartbeat(
    int Pid,
    string Kind,
    DateTime StartedAt,
    DateTime LastBeatAt)
{
    public const string DefaultKind = "watch";
    public const string ServeWatchKind = "serve-watch";
    public static readonly TimeSpan BeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);

    public static string PathFor(string storeDir, string kind) => Path.Combine(storeDir, $"{kind}.heartbeat");
}
