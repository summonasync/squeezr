namespace Squeezr.Core.Indexing;

public sealed record RepoIndexResult(
    string Name, int Added, int Modified, int Deleted, int Skipped, int Files, int Ignored,
    IReadOnlyDictionary<string, int>? IgnoredBySource = null);
