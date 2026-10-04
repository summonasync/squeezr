namespace Squeezr.Core.Storage;

public sealed record OrphanScanResult(
    IReadOnlyList<OrphanInfo> Orphans,
    IReadOnlyList<HuskInfo> Husks,
    IReadOnlyList<StaleInfo> Stale);
