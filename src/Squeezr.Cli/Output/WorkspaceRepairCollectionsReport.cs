namespace Squeezr.Cli.Output;

public sealed record WorkspaceRepairCollectionEntry(
    string Endpoint,
    string Collection,
    string Action,
    string? Failure = null);

// The collection arm of one repair pass. Endpoints is what the ledger covers; UnreachableEndpoints is the
// honest half — a sweep that could not answer says which endpoints it never saw, instead of reporting an
// empty reap set as "clean". SeenMinted is the denominator of that claim: collections carrying this
// product's own name that were checked for a row, so "none unreferenced" is never a green from zero items.
public sealed record WorkspaceRepairCollectionsReport(
    bool Ran,
    bool Apply,
    IReadOnlyList<string> Endpoints,
    int SeenMinted,
    IReadOnlyList<WorkspaceRepairCollectionEntry> Entries,
    IReadOnlyList<string> UnreachableEndpoints,
    string? SkipReason = null);
