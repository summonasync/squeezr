namespace Squeezr.Cli.Output;

public sealed record WorkspaceRepairCollectionEntry(
    string Endpoint,
    string Collection,
    string Action,
    string? Failure = null,
    string? Reason = null);

// An endpoint that was named by a live config and answered with nothing — the measured failure, not a
// guessed one. The sweep reports the error its own request caught so a reader can tell "port has no
// listener" from "collection set is empty"; without it a dead endpoint and a clean endpoint look identical.
public sealed record WorkspaceRepairEndpointFault(
    string Endpoint,
    string Error);

// The collection arm of one repair pass. Endpoints is what the ledger covers; UnreachableEndpoints is the
// honest half — a sweep that could not answer says which endpoints it never saw, instead of reporting an
// empty reap set as "clean". SeenMinted is the denominator of that claim: collections carrying this
// product's own name that were checked for a row, so "none unreferenced" is never a green from zero items.
// NotOwnedEndpoints and EndpointFaults are the two counted refusals: an endpoint this profile does not own
// is walked and reported but never deleted on, and an endpoint that never answered is named with its own
// error. Neither is a zero — a skipped count and a fault count are facts about the run, and a reader who
// misses them is missing a named field, not an absence.
public sealed record WorkspaceRepairCollectionsReport(
    bool Ran,
    bool Apply,
    IReadOnlyList<string> Endpoints,
    int SeenMinted,
    IReadOnlyList<WorkspaceRepairCollectionEntry> Entries,
    IReadOnlyList<string> UnreachableEndpoints,
    string? SkipReason = null,
    IReadOnlyList<string>? NotOwnedEndpoints = null,
    IReadOnlyList<WorkspaceRepairEndpointFault>? EndpointFaults = null);
