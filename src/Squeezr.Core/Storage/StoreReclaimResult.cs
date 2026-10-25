namespace Squeezr.Core.Storage;

// The numbers of one physical store reclaim. Sizes are the operator's proof that space came back;
// SkippedReason carries the failure text when the engine refused, so the pass says so instead of
// reporting a reclaim that did not happen. SnapshotCopiesLeft counts the rebuild snapshots that stayed
// next to the store — a copy of the pre-rebuild file is a copy of everything the purge just retired.
// RebuildMode says how the store was rebuilt: the engine's own rebuild, or the copy-out fallback that
// takes over when the engine refuses — FallbackNote then carries the engine's original refusal.
public sealed record StoreReclaimResult(
    long SizeBeforeBytes,
    long SizeAfterBytes,
    bool Reclaimed,
    string? SkippedReason,
    int SnapshotCopiesLeft = 0,
    string RebuildMode = "engine-rebuild",
    string? FallbackNote = null)
{
    public const string EngineRebuildMode = "engine-rebuild";
    public const string CopyOutFallbackMode = "copy-out-fallback";

    public long BytesFreed => Math.Max(0, SizeBeforeBytes - SizeAfterBytes);
}
