namespace Squeezr.Cli.Commands;

// One endpoint the collection survey walks, with the ownership fact that decides whether anything on it can
// be deleted. Ownership is not a machine-level guess: it is what the row's own config declared, so two
// profiles on this box contribute their own endpoints and never answer for each other's. An endpoint one row
// calls its own is ours for this sweep even if another row names the same address without the flag — the
// declaring row is consent, the absent flag on a sibling is not a veto over it.
internal sealed record QdrantSweepTarget(string Endpoint, bool Owned);
