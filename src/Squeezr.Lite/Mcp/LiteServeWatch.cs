using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Mcp;

// Lite's half of the one-indexer-per-store rule (070): serve --watch acquires the store's serve-watch claim
// and only the winner runs passes. The loser keeps answering queries and asks for the claim on the heartbeat
// interval, so a dead owner's store goes back to being watched without a human in the loop. The watcher is
// constructed with no vector phase at all — there is no embedding executor in this process, passes index the
// exact arms only, and the watcher keeps the embed-free zero-pass idle shape (070/T5).
internal sealed class LiteServeWatch : IAsyncDisposable
{
    private readonly string _workspaceRoot;
    private readonly TextWriter _stderr;
    private readonly WatchOwnerClaim? _claimOverride;
    private readonly TimeSpan? _adoptPollInterval;
    private WatchOwnerClaim? _claim;
    private WatchClaimAdopter? _adopter;
    private WorkspaceWatcher? _watcher;

    private LiteServeWatch(
        string workspaceRoot,
        TextWriter stderr,
        WatchOwnerClaim? claimOverride,
        TimeSpan? adoptPollInterval)
    {
        _workspaceRoot = workspaceRoot;
        _stderr = stderr;
        _claimOverride = claimOverride;
        _adoptPollInterval = adoptPollInterval;
    }

    // Test seams, mirroring the claim seam the full serve carries: one process cannot hold two claims by
    // pid, so a second-participant scenario hands in a claim built with the claim's seam-only ctor, and a
    // harness speeds the adoption poll past the heartbeat interval. Production passes both nulls.
    public static LiteServeWatch For(
        string workspaceRoot,
        TextWriter stderr,
        WatchOwnerClaim? claimOverride = null,
        TimeSpan? adoptPollInterval = null) =>
        new(workspaceRoot, stderr, claimOverride, adoptPollInterval);

    public bool IsIndexing => _watcher is not null;

    public async Task StartAsync(CancellationToken ct)
    {
        var claim = _claimOverride ?? new WatchOwnerClaim(
            SqueezrPaths.StoreDir(_workspaceRoot),
            DaemonHeartbeat.ServeWatchKind,
            onWarning: message => _stderr.WriteLine($"warning: sqzrlite serve watch: {message}"));
        _claim = claim;

        var outcome = claim.TryAcquire();
        if (outcome.Status == WatchOwnerStatus.RaceLost)
        {
            outcome = claim.TryAcquire();
        }

        if (!outcome.IsOwner)
        {
            _stderr.WriteLine(RefusalLine(outcome));
            ArmAdopter(claim, ct);
            return;
        }

        await StartWatcherAsync(claim, adoptedFrom: null, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_adopter is not null)
        {
            await _adopter.DisposeAsync();
            _adopter = null;
        }

        if (_watcher is not null)
        {
            await _watcher.StopAsync();
            _watcher = null;
            _claim = null;
            return;
        }

        Interlocked.Exchange(ref _claim, null)?.Release();
    }

    private void ArmAdopter(WatchOwnerClaim claim, CancellationToken ct)
    {
        _adopter = new WatchClaimAdopter(
            claim,
            outcome => AdoptAsync(claim, outcome, ct),
            pollInterval: _adoptPollInterval,
            onWarning: message => _stderr.WriteLine($"warning: sqzrlite serve watch: {message}"));
        _adopter.Start();
    }

    private async Task AdoptAsync(WatchOwnerClaim claim, WatchOwnerOutcome outcome, CancellationToken ct)
    {
        try
        {
            await StartWatcherAsync(claim, outcome.TookOverFrom, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _stderr.WriteLine($"warning: sqzrlite serve watch: claim adopted but the engine failed to start: {ex.Message}");
            claim.Release();
        }
    }

    private async Task StartWatcherAsync(WatchOwnerClaim claim, WatchOwnerMarker? adoptedFrom, CancellationToken ct)
    {
        var watcher = new WorkspaceWatcher(
            new LiteWatchStatus(_stderr), onStaleTakeover: StaleLockWarningSink.ToStderr, claim: claim);

        try
        {
            await watcher.StartAsync(_workspaceRoot, ct, daemonKind: DaemonHeartbeat.ServeWatchKind);
        }
        catch
        {
            claim.Release();
            throw;
        }

        _adopter = null;
        _watcher = watcher;
        _stderr.WriteLine(adoptedFrom is null
            ? "sqzrlite serve: watch engine running (exact arms only — no embeddings, no vector store)"
            : $"sqzrlite serve: watch engine running (claim adopted from dead pid {adoptedFrom.Pid})");
    }

    private string RefusalLine(WatchOwnerOutcome outcome)
    {
        var holder = outcome.Owner is { } owner ? $"pid {owner.Pid}" : "another process";
        return $"sqzrlite serve: watch claim held by {holder} — this serve answers queries only and indexes " +
               "nothing; it takes the loop over if that owner dies";
    }
}
