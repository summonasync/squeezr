namespace Squeezr.Core.Workspace;

// What a store's own record says, handed to a reader that must tell a measurement this call took from a
// measurement the store remembers. The three questions the marker answers keep three fields here and never
// collapse: whether a copy ever filled this store, whether the exact arm came with it, and whether a pass
// over this root has closed since. 'CoveredChunks' stays null when the accounting was never measured, which
// is a different fact from being measured at zero.
public sealed record WorktreeRecord(
    bool Filled,
    bool? ExactArmLanded,
    bool PassCompleted,
    bool? SemanticArmLanded,
    int ObligationChunks,
    int? CoveredChunks,
    string? Code,
    bool Pending,
    DateTimeOffset WrittenAt)
{
    public static WorktreeRecord? Of(ReconcileMarker? marker) => marker is null
        ? null
        : new WorktreeRecord(
            marker.Filled,
            marker.ExactArmLanded,
            marker.PassCompleted,
            marker.SemanticArmLanded,
            marker.ObligationChunks,
            marker.CoveredChunks,
            marker.Code,
            marker.Pending,
            marker.WrittenAt);
}
