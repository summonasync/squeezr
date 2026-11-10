using System.Text.Json.Serialization;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// The store the verb just produced or found, with everything an agent needs to keep working there:
// the key, the collection vectors will live in, and the serve line that binds this store.
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
    string? RepairHint)
{
    public static WorktreeInitReport From(WorktreeRegistrationOutcome outcome) =>
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
            ReconcilePending: outcome.ReconcilePending,
            Reason: outcome.Reason,
            RepairHint: outcome.RepairHint);
}
