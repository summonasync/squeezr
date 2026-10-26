namespace Squeezr.Core.Workspace;

// GuardWithheld/GuardWithheldBuiltIns/GuardDropped are the secret guard's ingestion tally as the last pass
// left it (064): paths refused, the built-in half of those, and stored rows the same decision removed.
public sealed record RepoStatus(
    string Name, string Path, int Files, int Symbols, int Chunks, DateTime? LastIndexedAt,
    int IgnoredTotal = 0,
    IReadOnlyDictionary<string, int>? IgnoredBySource = null,
    int GuardWithheld = 0,
    int GuardWithheldBuiltIns = 0,
    int GuardDropped = 0,
    int TrackedButExcluded = 0);
