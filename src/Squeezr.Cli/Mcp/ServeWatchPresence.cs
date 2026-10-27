using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Cli.Mcp;

// What a serve process holds for its watch standing, either half of it: the indexing engine when it won the store
// claim, a bare presence beat when it did not. The observer half can grow the engine underneath it — that is the
// adoption, a session that kept asking for the claim until the owner died. Both halves answer the question the
// fleet asks — is this pid alive on this store — which is why an observer that runs no pass still publishes, and
// why one door closes whichever standing the process ended in.
internal sealed class ServeWatchPresence : IAsyncDisposable
{
    private readonly object _gate = new();
    private WorkspaceWatcher? _engine;
    private DaemonHeartbeatWriter? _observerBeat;
    private WatchClaimAdopter? _adoption;
    private bool _disposed;

    private ServeWatchPresence()
    {
    }

    public static ServeWatchPresence Indexing(WorkspaceWatcher engine)
    {
        var presence = new ServeWatchPresence();
        presence._engine = engine;
        return presence;
    }

    public static ServeWatchPresence Observing(DaemonHeartbeatWriter observerBeat)
    {
        var presence = new ServeWatchPresence();
        presence._observerBeat = observerBeat;
        return presence;
    }

    public void AdoptClaimsThrough(WatchClaimAdopter adoption)
    {
        lock (_gate)
        {
            if (_disposed || _adoption is not null)
            {
                throw new InvalidOperationException("Watch standing is already decided for this presence.");
            }

            _adoption = adoption;
        }
    }

    // Called from the adoption loop the moment its claim attempt won: the watcher already owns the claim and
    // runs, the presence only moves its disposal half from the observer beat to the engine. The adopter stays
    // registered (its loop has finished by contract) so DisposeAsync still reaps its CTS. False means serve
    // began shutting down while the adoption was arming — the engine is then the armer's to stop.
    public bool Promote(WorkspaceWatcher engine)
    {
        DaemonHeartbeatWriter? beat;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _engine = engine;
            beat = _observerBeat;
            _observerBeat = null;
        }

        beat?.Dispose();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        WatchClaimAdopter? adoption;
        WorkspaceWatcher? engine;
        DaemonHeartbeatWriter? beat;
        lock (_gate)
        {
            _disposed = true;
            adoption = _adoption;
            _adoption = null;
            engine = _engine;
            _engine = null;
            beat = _observerBeat;
            _observerBeat = null;
        }

        if (adoption is not null)
        {
            await adoption.DisposeAsync();
        }

        if (engine is not null)
        {
            await engine.StopAsync();
            return;
        }

        beat?.Dispose();
    }
}
