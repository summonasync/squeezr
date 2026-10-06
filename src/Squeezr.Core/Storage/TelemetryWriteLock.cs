using System.Diagnostics;

namespace Squeezr.Core.Storage;

// queries.jsonl gets a line from every cli read and every mcp tool call, and the compact scrub rewrites the
// whole file. The lock is what makes those writers queue instead of a swap publishing away a line that landed
// mid-scrub. FileShare.None is dropped by the os when its process dies, so a crashed writer never wedges the
// log and no stale-owner takeover is needed the way index.lock does it.
public sealed class TelemetryWriteLock : IDisposable
{
    public const string FileName = "queries.lock";

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    private const int RetryDelayMs = 1;

    private readonly FileStream _stream;

    private TelemetryWriteLock(FileStream stream) => _stream = stream;

    public static string LockPath(string storeDir) => Path.Combine(storeDir, FileName);

    public static TelemetryWriteLock? TryAcquire(string storeDir)
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
                if (Stopwatch.GetElapsedTime(started) >= DefaultWait)
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
