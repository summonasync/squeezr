using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Cli.Commands;

internal sealed class ClaimGraceAwaiter
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(2);

    private readonly Func<DateTime> _utcNow;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Action<TimeSpan> _delay;

    public ClaimGraceAwaiter(Action<TimeSpan> delay)
        : this(() => DateTime.UtcNow, DaemonHeartbeatReader.IsProcessAlive, delay)
    {
    }

    internal ClaimGraceAwaiter(Func<DateTime> utcNow, Func<int, bool> isProcessAlive, Action<TimeSpan> delay)
    {
        _utcNow = utcNow;
        _isProcessAlive = isProcessAlive;
        _delay = delay;
    }

    public WatchOwnerOutcome Await(WatchOwnerClaim claim, WatchOwnerOutcome held)
    {
        if (held.Owner is not { } holder || _isProcessAlive(holder.Pid))
        {
            return held;
        }

        var deadline = holder.LastBeatAt + DaemonHeartbeat.StaleThreshold + ExpiryMargin;
        while (_utcNow() < deadline && claim.ReadOwner() is { } current)
        {
            if (current.IsBeatStale(_utcNow()) || _isProcessAlive(current.Pid))
            {
                break;
            }

            _delay(PollInterval);
        }

        return claim.TryAcquire();
    }
}
