namespace Squeezr.Core.Configuration;

public sealed record IndexingConfig
{
    public required IReadOnlyList<string> Ignore { get; init; }

    public required int MaxFileSizeKb { get; init; }

    public bool NoSemantic { get; init; }

    public IReadOnlyList<string> RankExclude { get; init; } = [];
}
