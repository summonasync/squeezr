namespace Squeezr.Core.Storage;

// Rows written before the worktree fields carry none of them, so all three are null and the
// WhenWritingNull contract keeps such a row byte-for-byte the shape the old build reads.
public sealed record WorkspaceRegistryEntry(
    string Name,
    string RootPath,
    string StoreKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    string? ParentRootPath = null,
    WorkspaceKind? Kind = null,
    string? Branch = null);
