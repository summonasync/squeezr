namespace Squeezr.Cli.Output;

public sealed record RepoStatusReport(
    string Name,
    string Path,
    int Files,
    int Symbols,
    int Chunks,
    IReadOnlyList<string> StaleFiles,
    DateTime? LastIndexedAt,
    TimeSpan? IndexAge);
