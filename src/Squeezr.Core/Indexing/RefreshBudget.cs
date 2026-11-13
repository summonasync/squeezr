namespace Squeezr.Core.Indexing;

// The two caps one CheckAndRefresh call may spend re-reading files. Measured on a real stand (870 rows,
// 4.7 symbols/file, this guard's own check path) and on the symbol-dense polygon, per file; each row is the
// 2026-11-10 pass and the 2026-11-11 post-boot re-run of the same harness (audits/017-t4-2026-11-10/), and
// the ranges below are the union of the two, which is what a per-file price actually is on this box:
//
//   check, one candidate                    0.37-1.50 ms   (0.44 median per candidate on the re-run)
//   re-read, one non-C# file              162-273 ms  (median 177 / 179.66)
//   re-read, one C# file               2 749-7 596 ms  (median 3 641 / 3 598.63 — the route opens the
//                                                      solution per file, so the price is per file and
//                                                      never amortised: 5 files cost 15.6-17.3 s)
//   re-read, one C# file on the polygon 26 107-34 027 ms
//
// MaxWallClockMillis sits deliberately between the two measured prices: it is passed inside one expensive
// re-read, so a call over source files heals exactly one and refuses the rest, and it admits a handful of
// cheap ones, so a call over docs and configs stays useful. MaxFilesReRead is the outer wall for the cheap
// regime — at the measured 177-180 ms, four re-reads cost 708-719 ms, so the two caps land on the same
// count where the cheap path lives and neither cap has to do the other's job.
//
// MaxFilesReRead counts re-reads *started*, not re-reads that succeeded: a route that threw spent the same
// budget as one that wrote a row. The wall bound is read at one deterministic point, immediately before a
// re-read would start, and it never refuses the first one of a call — a slow store open must not turn the
// budget into the reason a call healed nothing, which is the shape the answer is not allowed to attribute.
// Nothing interrupts a re-read already under way, so a call overshoots the bound by at most one file: that
// residual is the price of not pushing a cancellation token into the core, and it belongs to 032.
public static class RefreshBudget
{
    public const int MaxFilesReRead = 4;
    public const int MaxWallClockMillis = 750;
}
