using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// A vanished checkout is one measured fact that two verbs have to answer: `worktree drop` refuses to reap a tree
// that is already gone, and `worktree resolve` — the read door a session hook calls — owes the same answer
// without becoming a second story about it. So the facts live here as data: the path as it was asked, the
// directory its link walk reaches, and every registry row that still names that directory.
//
// There is deliberately no sentence on this record: each verb composes its own remedy from these fields, because
// only the door that was asked to act can say what it would have done. What the record carries on the wire is
// what was measured — a reader of a refusal has to tell 'gone and nobody claims it' from 'gone and N rows still
// claim it, under these store keys', and which keys survive is exactly what that reader must decide about next.
public sealed record WorktreeGoneTree(string Path, string PhysicalPath, IReadOnlyList<WorktreeGoneRow> Rows)
{
    public static WorktreeGoneTree Measure(string path, string physicalPath) =>
        new(path, physicalPath, [.. WorkspaceRegistry.Load()
            .Where(row => WorkspaceCanonicalPath.SamePhysicalPath(row.RootPath, physicalPath))
            .Select(WorktreeGoneRow.Of)]);
}

public sealed record WorktreeGoneRow(string StoreKey, string Name, string RootPath)
{
    public static WorktreeGoneRow Of(WorkspaceRegistryEntry row) => new(row.StoreKey, row.Name, row.RootPath);
}
