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

    // The one read door onto the claim, shared by whoever holds it and by readers who only name it (fleet,
    // mcp doctor): a marker that cannot be read answers null, never a guessed owner.
    public static WatchOwnerMarker? TryRead(string storeDir) =>
        WatchOwnerMarkerFile.Read(PathFor(storeDir)) is (WatchOwnerMarkerState.Present, { } marker) ? marker : null;

    public bool IsOwnedBy(int pid, string host) =>
        Pid == pid && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase);

    // Which beat on this store belongs to the claim owner. A beat written by a binary older than the per-pid
    // carrier names no host, so its pid is all there is to compare; a beat that does name a host has to name
    // this one too, because a pid from another machine's namespace proves nothing here.
    public bool OwnsBeat(DaemonHeartbeat beat) =>
        beat.Pid == Pid
        && (beat.Host is null || string.Equals(beat.Host, Host, StringComparison.OrdinalIgnoreCase));

    public bool IsBeatStale(DateTime utcNow) =>
        utcNow - LastBeatAt > DaemonHeartbeat.StaleThreshold;

    public bool IsPastForeignWindow(DateTime utcNow) =>
        utcNow - LastBeatAt > ForeignHostStaleWindow;

    public DaemonHeartbeat AsHeartbeat() => new(Pid, Kind, StartedAt, LastBeatAt);
}
