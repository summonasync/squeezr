namespace Squeezr.Core.Configuration;

public sealed record IndexingConfig
{
    public required IReadOnlyList<string> Ignore { get; init; }

    public required int MaxFileSizeKb { get; init; }

    public required bool GitAware { get; init; }
}
