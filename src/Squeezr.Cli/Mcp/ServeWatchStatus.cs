using System.Text.Json;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Cli.Mcp;

// serve --watch without --events used to run with no status sink at all, so the pass report was dropped
// before anyone could read it — exactly the pass shape this task exists for (a watch deletion never has a
// vector store). Reap debt is what this arm must never swallow: the queue line is machine-readable, and a
// fault repeats only when it changes so a broken daemon cannot fill the log with the same sentence.
internal sealed class ServeWatchStatus(TextWriter output) : IWatchStatus
{
    private string? _lastFault;

    public void PassStarted(int changedPaths)
    {
    }

    public void PassCompleted(IndexResult result)
    {
        if (result.DeferredReaps == 0 && result.PendingReaps == 0)
        {
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

    private sealed record WatchReapReport(string Type, int DeferredReaps, int PendingReaps);
}
