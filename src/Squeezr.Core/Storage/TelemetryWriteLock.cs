using System.Diagnostics;

namespace Squeezr.Core.Storage;

// queries.jsonl gets a line from every cli read and every mcp tool call, and the compact scrub rewrites the
// whole file. The lock is what makes those writers queue instead of a swap publishing away a line that landed
// mid-scrub. FileShare.None is dropped by the os when its process dies, so a crashed writer never wedges the
// log and no stale-owner takeover is needed the way index.lock does it.
public sealed class TelemetryWriteLock : IDisposable
{
    public const string FileName = "queries.lock";

    internal static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    // The second chance for a writer that missed the first budget. Under the swap-only lock a pass holds the log
    // for two byte reads and a rename, so this is three orders of magnitude of margin against a real swap; it
    // exists so a late row is still written *inside* the exclusion instead of on top of somebody's row outside it.
    internal static readonly TimeSpan LateAppendWait = TimeSpan.FromSeconds(4);

    private const int RetryDelayMs = 1;

    private readonly FileStream _stream;

    private TelemetryWriteLock(FileStream stream) => _stream = stream;

    public static string LockPath(string storeDir) => Path.Combine(storeDir, FileName);

    public static TelemetryWriteLock? TryAcquire(string storeDir) => TryAcquire(storeDir, DefaultWait);

    // the wait is a parameter because the two stages of an append want different things from it: the first is a
    // queue behind a scrub, the second is the writer saying "I am late, but I will still write inside the
    // exclusion rather than on top of somebody else's row outside it"
    public static TelemetryWriteLock? TryAcquire(string storeDir, TimeSpan wait)
    {
        try
        {
            Directory.CreateDirectory(storeDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var path = LockPath(storeDir);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new TelemetryWriteLock(
                    new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None));
            }
            catch (IOException)
            {
                if (Stopwatch.GetElapsedTime(started) >= wait)
                {
                    return null;
                }

                Thread.Sleep(RetryDelayMs);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
