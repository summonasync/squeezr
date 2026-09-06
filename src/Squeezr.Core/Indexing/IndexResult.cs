namespace Squeezr.Core.Indexing;

public sealed record IndexResult(IReadOnlyList<RepoIndexResult> Repos, DateTime IndexedAt, IReadOnlyList<string> Warnings);
