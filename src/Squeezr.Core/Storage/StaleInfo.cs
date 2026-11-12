namespace Squeezr.Core.Storage;

// ParentRootPath rides along for a row registered as a worktree child. The dead root is the same fact for a
// lane and for a plain folder, but a reader deciding whether the sweep is safe wants to know which dead root
// belonged to a tree whose parent is still indexed.
public sealed record StaleInfo(string Name, string StoreKey, string RootPath, string? ParentRootPath = null);
