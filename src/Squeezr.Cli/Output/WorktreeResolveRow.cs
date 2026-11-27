using System.Text.Json.Serialization;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// One registry row of the resolved tree, as the read verb serves it: the columns T1 put on the row, the two
// presence facts that were measured against the disk, and the two names a reader needs to keep working in that
// store — the collection its vectors live in and the serve line that binds it. The collection name is a hash of
// the root, so it is computed rather than asked: no Qdrant round trip belongs on a read door, and an answer about
// a stopped service must not depend on the service answering.
//
// The record and its notices ride the row and not the payload, because one checkout can hold two rows under two
// spellings with two stores and two reconcile proofs. Folding two verdicts into one word is how a lane starts
// reading another store's evidence as its own; the notices come from ReconcilePendingNotice, the one door every
// surface shares, so this verb adds no second pending vocabulary.
public sealed record WorktreeResolveRow(
    string StoreKey,
    string Name,
    string RootPath,
    WorkspaceKind? Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Branch,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ParentRootPath,
    bool TreePresent,
    bool StoreDirPresent,
    string Collection,
    string ServeCommand,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorktreeRecord? Record,
    IReadOnlyList<string> Notices)
{
    public static WorktreeResolveRow Of(WorktreeResolvedRow row) =>
        new(
            row.StoreKey,
            row.Name,
            row.RootPath,
            row.Kind,
            row.Branch,
            row.ParentRootPath,
            row.TreePresent,
            row.StoreDirPresent,
            QdrantVectorStore.GetCollectionName(row.RootPath),
            RemedyText.RebindServe(row.RootPath),
            row.Record,
            ReconcilePendingNotice.Lines(row.Record));
}
