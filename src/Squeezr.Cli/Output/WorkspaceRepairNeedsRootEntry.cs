namespace Squeezr.Cli.Output;

// One bucket, two causes, because both mean the same thing to the operator: repair stops here and the
// decision is theirs. A store with no recorded root needs --root; a store whose recorded root is gone
// needs that directory back before anything can adopt it.
public sealed record WorkspaceRepairNeedsRootEntry(string Name, string StoreKey, string? RootPath, string Reason);
