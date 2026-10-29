namespace Squeezr.Core.Indexing.Watch.Supervision;

public interface IWatchDaemonHandle : IDisposable
{
    int Id { get; }

    string? BinaryPath { get; }

    DateTime? StartedUtc { get; }

    bool HasExited { get; }

    void Stop();
}
