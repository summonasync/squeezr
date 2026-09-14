using System.Diagnostics;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Cli.Output;

public sealed class WatchEventsStatus : IWatchStatus
{
    private readonly IndexEventWriter _writer;
    private readonly string _workspaceRoot;
    private readonly IndexEventsRenderer _progress;
    private long _passStartedAt;

    public WatchEventsStatus(IndexEventWriter writer, string workspaceRoot, IndexEventsRenderer progress)
    {
        _writer = writer;
        _workspaceRoot = workspaceRoot;
        _progress = progress;
    }

    public void PassStarted(int changedPaths)
    {
        _progress.Reset();
        _passStartedAt = Stopwatch.GetTimestamp();
        _writer.Started(_workspaceRoot, changedPaths);
    }

    public void PassCompleted(IndexResult result)
    {
        var durationMs = (int)Stopwatch.GetElapsedTime(_passStartedAt).TotalMilliseconds;
        _writer.Completed(result, durationMs, _progress.Symbols);
    }

    public void RetryScheduled(TimeSpan delay)
    {
    }

    public void Faulted(string error) => _writer.Error("internal_error", error);
}
