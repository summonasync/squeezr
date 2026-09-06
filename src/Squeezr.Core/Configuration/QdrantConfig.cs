namespace Squeezr.Core.Configuration;

public sealed record QdrantConfig
{
    public required string Endpoint { get; init; }
}
