namespace Squeezr.Core.Storage;

public sealed record IndexState
{
    public int Version { get; init; } = 1;

    public Dictionary<string, RepoIndexCursor> Repos { get; init; } = new();
}
