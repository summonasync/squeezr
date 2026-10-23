using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Cli.Mcp;

// What a serve process holds for its watch standing, either half of it: the indexing engine when it won the store
// claim, a bare presence beat when it did not. Both halves answer the question the fleet asks — is this pid alive
// on this store — which is why an observer that runs no pass still publishes, and why one door closes both.
internal sealed class ServeWatchPresence : IAsyncDisposable
{
    private readonly WorkspaceWatcher? _engine;
    private readonly DaemonHeartbeatWriter? _observerBeat;

    private ServeWatchPresence(WorkspaceWatcher? engine, DaemonHeartbeatWriter? observerBeat)
    {
        _engine = engine;
        _observerBeat = observerBeat;
    }

    public static ServeWatchPresence Indexing(WorkspaceWatcher engine) => new(engine, null);

    public static ServeWatchPresence Observing(DaemonHeartbeatWriter observerBeat) => new(null, observerBeat);

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.StopAsync();
            return;
        }

        _observerBeat?.Dispose();
    }
}
