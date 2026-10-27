namespace Squeezr.Core.Indexing.Watch;

/// <summary>
/// The observer's half of watch ownership: a session that found the store owned keeps asking for the claim
/// until the owner is provably gone, then takes it through the ordinary acquire rules. Liveness comes from
/// the claim itself — beat past the stale window under a dead pid, never a fresh beat or a wedged one — so
/// an adoption can only ever follow a death, and two adopting sessions on one store are arbitrated by the
/// marker's create. Single-shot by contract: after the first adoption the claim belongs to the session and
/// the watcher owns the loop's lifecycle, promote failure included.
/// </summary>
public sealed class WatchClaimAdopter : IAsyncDisposable
{
    public static readonly TimeSpan DefaultPollInterval = DaemonHeartbeat.BeatInterval;

    private readonly WatchOwnerClaim _claim;
    private readonly Func<WatchOwnerOutcome, Task> _onAdopted;
    private readonly TimeSpan _pollInterval;
    private readonly Action<string>? _onWarning;
    private readonly object _gate = new();
    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;

    public WatchClaimAdopter(
        WatchOwnerClaim claim,
        Func<WatchOwnerOutcome, Task> onAdopted,
        TimeSpan? pollInterval = null,
        Action<string>? onWarning = null)
    {
        _claim = claim;
        _onAdopted = onAdopted;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _onWarning = onWarning;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_stopping is not null)
            {
                throw new InvalidOperationException("Watch claim adopter is already running.");
            }

            _stopping = new CancellationTokenSource();
            _loop = RunAsync(_stopping.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? stopping;
        Task loop;
        lock (_gate)
        {
            stopping = _stopping;
            _stopping = null;
            loop = _loop;
        }

        if (stopping is null)
        {
            return;
        }

        stopping.Cancel();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            stopping.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                var outcome = _claim.TryAcquire();
                if (!outcome.IsOwner)
                {
                    continue;
                }

                await _onAdopted(outcome).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _onWarning?.Invoke($"watch claim adoption stopped: {ex.Message}");
        }
    }
}
