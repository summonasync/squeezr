namespace Squeezr.Core.Indexing.Watch;

public sealed record DaemonHeartbeat(
    int Pid,
    string Kind,
    DateTime StartedAt,
    DateTime LastBeatAt,
    string? Host = null,
    DaemonActivity? Activity = null)
{
    // The activity tail is additive on purpose: a reader built before it drops the unknown member, and an
    // idle daemon omits it entirely (WhenWritingNull), so old beats and idle beats share one file shape.

    public const string DefaultKind = "watch";
    public const string ServeWatchKind = "serve-watch";
    public const string BeatsDirName = "beats";
    public const string BeatExtension = ".beat";
    public static readonly TimeSpan BeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);

    // One file per (store, kind) answering "who runs the watch loop here": only the live claim owner writes it,
    // and a writer demoted from the claim stops stamping the moment ownership moves on and drops the file while
    // it still names that writer, so a reader from before the per-pid carrier finds one truthful pid or none —
    // never whichever writer raced last. The full roster of live processes lives in beats/.
    public static string PathFor(string storeDir, string kind) => Path.Combine(storeDir, $"{kind}.heartbeat");

    public static string BeatsDirFor(string storeDir) => Path.Combine(storeDir, BeatsDirName);

    public static string BeatPathFor(string storeDir, string kind, int pid) =>
        Path.Combine(BeatsDirFor(storeDir), BeatFileName(kind, pid));

    public static string BeatFileName(string kind, int pid) => $"{kind}.{pid}{BeatExtension}";

    // The kind is everything before the last dotted pair, so "serve-watch.4711.beat" reads back as
    // ("serve-watch", 4711). A name outside that shape — a temp file, a stranger dropped by hand — is no beat.
    public static bool TryParseBeatFileName(string fileName, out string kind, out int pid)
    {
        kind = string.Empty;
        pid = 0;
        if (!fileName.EndsWith(BeatExtension, StringComparison.Ordinal))
        {
            return false;
        }

        var body = fileName[..^BeatExtension.Length];
        var separator = body.LastIndexOf('.');
        if (separator <= 0 || separator == body.Length - 1)
        {
            return false;
        }

        kind = body[..separator];
        return int.TryParse(body[(separator + 1)..], out pid) && pid > 0;
    }
}
