namespace Squeezr.Core.Storage;

public sealed record OrphanInfo(string StoreKey, string Name, string? RootPath)
{
    // Owner ruling (variant a, 2026-10-06): an adoption without a live root is an instantly stale row,
    // so repair never adopts a store whose recorded root is gone. The scan snapshots liveness once; the
    // sweep reads the same fact instead of re-probing the disk.
    public bool RootExists { get; init; }

    public bool IsAdoptable => RootPath is not null && RootExists;
}
