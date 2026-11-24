namespace Squeezr.Core.Indexing;

// The two caps one CheckAndRefresh call may spend on the batch it starts. The prices below are why the
// batch exists: they are the per-file route it replaced — a solution open per re-read — measured on a real
// stand (870 rows, 4.7 symbols/file, this guard's own check path) and on the symbol-dense polygon; each row
// is the 2026-11-10 pass and the 2026-11-11 post-boot re-run of the same harness, and
// the ranges below are the union of the two, which is what a per-file price actually is on this box:
//
//   check, one candidate                    0.37-1.50 ms   (0.44 median per candidate on the re-run)
//   re-read, one non-C# file              162-273 ms  (median 177 / 179.66)
//   re-read, one C# file               2 749-7 596 ms  (median 3 641 / 3 598.63 — the old route opened the
//                                                      solution per file, so the price was per file and
//                                                      never amortised: 5 files cost 15.6-17.3 s)
//   re-read, one C# file on the polygon 26 107-34 027 ms
//
// One call starts at most one batch, and the caps bound how much that batch may carry. MaxFilesReRead is
// the batch's candidate capacity: at most this many stale candidates are announced into it, and a candidate
// refused a slot is refused as itself. If a batch this wide swings too wide, the constant above is the
// batch size to change — stated here, never as a second meaning. MaxWallClockMillis is read at the same
// deterministic point it always was, immediately before another slot is accepted and never after the batch
// has begun, and it never refuses the first slot of a call — a slow store open must not turn the budget
// into the reason a call healed nothing. A slot is spent when it is announced, not when a row comes back:
// the budget counts work handed to the batch, whether the batch writes it, reaps it, or skips it.
//
// Nothing interrupts the batch once it starts, so a call overshoots the bound by one batch, not one file:
// that residual is the price of not pushing a cancellation token into the core, and it belongs to 032.
// A cancellation read at that same point collapses the whole call while nothing has been accepted — every
// candidate lands Stale, protected paths included, and that is intended: a protected path is never re-read,
// it only ever surfaces Stale.
public static class RefreshBudget
{
    public const int MaxFilesReRead = 4;
    public const int MaxWallClockMillis = 750;
}
