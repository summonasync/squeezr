using System.Text.Json.Serialization;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// The store the verb just produced or found, with everything an agent needs to keep working there: the key, the
// collection vectors will live in, and the serve line that binds this store.
//
// The baseline block is absent unless --baseline was asked (035: an absent key is not a false claim). When it is
// present it carries the cost sentence this verb prints BEFORE the pass that pays it, so the payload is the
// evidence that the price was named first — a report saying only 'baseline: registered' would let the ordering
// this flag ships under go unmeasured.
public sealed record WorktreeInitReport(
    string State,
    string RootPath,
    string? WorkspaceName,
    string? StoreKey,
    WorkspaceKind? Kind,
    string? ParentRootPath,
    string? Branch,
    string? Collection,
    string? ServeCommand,
    bool IsLinked,
    bool IsDetached,
    bool? ReconcilePending,
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RepairHint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorktreeFillReport? Fill,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorktreeReconcileReport? Reconcile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorktreeBaselineReport? Baseline)
{
    public static WorktreeInitReport From(
        WorktreeRegistrationOutcome outcome,
        WorktreeFillOutcome fill,
        WorktreeReconcileOutcome? reconcile = null,
        WorktreeBaselineOutcome? baseline = null) =>
        new(
            State: outcome.State.ToString(),
            RootPath: outcome.RootPath,
            WorkspaceName: outcome.WorkspaceName,
            StoreKey: outcome.StoreKey,
            Kind: outcome.Kind,
            ParentRootPath: outcome.ParentRootPath,
            Branch: outcome.Branch,
            Collection: outcome.StoreKey is null ? null : QdrantVectorStore.GetCollectionName(outcome.RootPath),
            ServeCommand: outcome.StoreKey is null ? null : RemedyText.RebindServe(outcome.RootPath),
            IsLinked: outcome.IsLinked,
            IsDetached: outcome.IsDetached,
            // The registration answers for a store it minted empty, so pending is a fact there; once a pass
            // has read this lane's own rows against its own collection, the verdict is the better answer and
            // it replaces the guess. A deferred reconcile reports no measurement, so the registration's word
            // stands, and the store's own record (reconcile.record) is what a later session start reads
            // instead of paying for the measurement again.
            ReconcilePending: reconcile?.State switch
            {
                WorktreeReconcileState.Reconciled => false,
                WorktreeReconcileState.Pending => true,
                _ => outcome.ReconcilePending,
            },
            Reason: outcome.Reason,
            RepairHint: outcome.RepairHint,
            Fill: WorktreeFillReport.From(fill),
            Reconcile: reconcile is null ? null : WorktreeReconcileReport.From(reconcile),
            Baseline: baseline is null ? null : WorktreeBaselineReport.From(baseline));
}
