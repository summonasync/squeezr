namespace Squeezr.Core.Indexing.Watch;

// A repo whose honest pass costs more than the base budget must converge instead of livelocking: every
// consecutive overrun doubles the armed budget up to the ceiling, and a pass that completes snaps the
// ladder back to the floor. In-memory on purpose — a daemon restart re-arms from the configured base.
// A non-positive floor is the immediate-abort seam the tests use ("cancel at the first boundary") and
// stays exactly that: doubling zero is still zero, so escalation can never un-arm the seam.
// The overrun is noted where the budget expired, not where the abort lands: a pass parked inside a
// syscall never reaches the abort, and that shape needs the larger window just as much.
internal sealed class PassBudgetLadder
{
    private readonly TimeSpan _floor;
    private readonly TimeSpan _ceiling;
    private long _armedTicks;

    public PassBudgetLadder(TimeSpan floor, TimeSpan ceiling)
    {
        _floor = floor;
        _ceiling = ceiling;
        Volatile.Write(ref _armedTicks, floor <= TimeSpan.Zero ? floor.Ticks : Math.Min(floor.Ticks, ceiling.Ticks));
    }

    public TimeSpan Armed => TimeSpan.FromTicks(Volatile.Read(ref _armedTicks));

    public void NoteOverrun()
    {
        if (_floor <= TimeSpan.Zero)
        {
            return;
        }

        Volatile.Write(ref _armedTicks, Math.Min(Armed.Ticks * 2, _ceiling.Ticks));
    }

    public void NoteCompleted()
    {
        Volatile.Write(ref _armedTicks, _floor.Ticks);
    }
}
