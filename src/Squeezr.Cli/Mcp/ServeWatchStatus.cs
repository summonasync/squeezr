using System.Text.Json;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Cli.Mcp;

// serve --watch used to run with no status sink at all, so the pass report was dropped before anyone
// could read it — exactly the pass shape this task exists for. Reap debt is what this arm must never
// swallow: the queue line is machine-readable, and a fault repeats only when it changes so a broken
// daemon cannot fill the log with the same sentence. Since the drain moved into the watch pass
// (055/T3), the same arm also reports how many pending chunks each pass embedded, plus the reason
// the phase stood idle (provider or vector store unavailable) so a stalled queue is distinguishable
// from a slow one.
internal sealed class ServeWatchStatus(TextWriter output) : IWatchStatus
{
    private string? _lastFault;
    private string? _lastIdleReason;

    public void PassStarted(int changedPaths)
    {
    }

    public void PassCompleted(IndexResult result)
    {
        var wrote = false;
        var idleReason = result.Warnings.FirstOrDefault(w =>
            w.StartsWith("embeddings skipped", StringComparison.Ordinal));
        if (result.Embedded > 0)
        {
            _lastIdleReason = null;
            output.WriteLine(JsonSerializer.Serialize(
                new WatchDrainReport("watchDrain", DateTime.UtcNow.ToString("o"), result.Embedded, null),
                SqueezrJson.OutputOptions));
            wrote = true;
        }
        else if (idleReason is not null && idleReason != _lastIdleReason)
        {
            output.WriteLine(JsonSerializer.Serialize(
                new WatchDrainReport("watchDrain", DateTime.UtcNow.ToString("o"), 0, idleReason),
                SqueezrJson.OutputOptions));
            _lastIdleReason = idleReason;
            wrote = true;
        }

        if (result.DeferredReaps == 0 && result.PendingReaps == 0)
        {
            if (wrote)
            {
                output.Flush();
            }

            return;
        }

        output.WriteLine(JsonSerializer.Serialize(
            new WatchReapReport("watchPass", result.DeferredReaps, result.PendingReaps), SqueezrJson.OutputOptions));
        output.Flush();
    }

    public void RetryScheduled(TimeSpan delay)
    {
    }

    public void Faulted(string error)
    {
        if (string.Equals(error, _lastFault, StringComparison.Ordinal))
        {
            return;
        }

        _lastFault = error;
        output.WriteLine($"warning: serve watch: {error}");
        output.Flush();
    }

    private sealed record WatchDrainReport(string Type, string At, int Embedded, string? IdleReason);

    private sealed record WatchReapReport(string Type, int DeferredReaps, int PendingReaps);
}
