using System.Diagnostics;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Lite.Mcp;

internal sealed class LiteWatchStatus(TextWriter output) : IWatchStatus
{
    private readonly Stopwatch _pass = new();
    private string? _lastFault;

    public void PassStarted(int changedPaths) => _pass.Restart();

    public void PassCompleted(IndexResult result)
    {
        var changed = result.Repos.Sum(r => r.Added + r.Modified + r.Deleted);
        if (changed == 0 && result.DeferredReaps == 0 && result.PendingReaps == 0)
        {
            _pass.Reset();
            return;
        }

        var reaps = result.DeferredReaps + result.PendingReaps;
        output.WriteLine(
            $"sqzrlite serve: watch pass +{Sum(result, r => r.Added)} ~{Sum(result, r => r.Modified)} " +
            $"-{Sum(result, r => r.Deleted)} (skipped: {result.Repos.Sum(r => r.Skipped)}," +
            (reaps > 0 ? $" reaps: {reaps}," : "") +
            $" {FormatDuration(_pass.Elapsed)})");
        _pass.Reset();
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
        output.WriteLine($"warning: sqzrlite serve watch: {error}");
        output.Flush();
    }

    private static int Sum(IndexResult result, Func<RepoIndexResult, int> pick) =>
        result.Repos.Sum(pick);

    private static string FormatDuration(TimeSpan elapsed) =>
        elapsed.TotalSeconds >= 1 ? $"{elapsed.TotalSeconds:n1}s" : $"{elapsed.TotalMilliseconds:n0}ms";
}
