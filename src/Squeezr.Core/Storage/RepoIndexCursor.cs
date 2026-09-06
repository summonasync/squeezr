namespace Squeezr.Core.Storage;

public sealed record RepoIndexCursor
{
    public string? LastIndexedCommit { get; init; }

    public DateTime? LastIndexedAt { get; init; }
}
