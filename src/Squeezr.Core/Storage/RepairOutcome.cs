namespace Squeezr.Core.Storage;

public sealed record RepairOutcome(
    IReadOnlyList<OrphanInfo> Adopted,
    IReadOnlyList<OrphanInfo> NeedsRoot,
    IReadOnlyList<HuskInfo> Husks);
