namespace Squeezr.Core.Indexing.Watch;

public sealed record WatchOwnerMarker(
    int Pid,
    string Host,
    string Kind,
    DateTime StartedAt,
    DateTime LastBeatAt)
{
    public const string MarkerFileName = "watch.owner";

    /// <summary>
    /// A marker naming another host carries a pid from that host's namespace, so local pid liveness says nothing
    /// about it and takeover is time-only: this multiple of <see cref="DaemonHeartbeat.StaleThreshold"/>.
    /// </summary>
    public const int ForeignHostStaleWindowMultiplier = 4;

    public static TimeSpan ForeignHostStaleWindow =>
        DaemonHeartbeat.StaleThreshold * ForeignHostStaleWindowMultiplier;

    public static string PathFor(string storeDir) => Path.Combine(storeDir, MarkerFileName);

    public bool IsOwnedBy(int pid, string host) =>
        Pid == pid && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase);

    public bool IsBeatStale(DateTime utcNow) =>
        utcNow - LastBeatAt > DaemonHeartbeat.StaleThreshold;

    public bool IsPastForeignWindow(DateTime utcNow) =>
        utcNow - LastBeatAt > ForeignHostStaleWindow;

    public DaemonHeartbeat AsHeartbeat() => new(Pid, Kind, StartedAt, LastBeatAt);
}
