namespace Squeezr.Core.Configuration;

public sealed record ChunkingConfig
{
    public required int MaxTokensPerChunk { get; init; }

    public required int FallbackWindowLines { get; init; }

    public required int FallbackOverlapLines { get; init; }
}
