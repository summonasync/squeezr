namespace Squeezr.Cli.Output;

public sealed record RepoStatusReport(
    string Name,
    string Path,
    int Files,
    int Symbols,
    int Chunks,
    IReadOnlyList<string> StaleFiles,
    DateTime? LastIndexedAt,
    TimeSpan? IndexAge,
    RepoIgnoredReport? Ignored = null);

public sealed record RepoIgnoredReport(
    int Files,
    IReadOnlyDictionary<string, int> BySource);
