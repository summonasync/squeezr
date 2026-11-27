namespace Squeezr.Semantic.Configuration;

public sealed record QdrantConfig
{
    public const int DefaultOwnedRestPort = 6335;
    public const int DefaultOwnedGrpcPort = 6336;

    // empty is legal when owned: the endpoint is then derived from RestPort
    public string Endpoint { get; init; } = string.Empty;

    public bool Owned { get; init; }

    public int RestPort { get; init; } = DefaultOwnedRestPort;

    public int GrpcPort { get; init; } = DefaultOwnedGrpcPort;

    public string ResolvedEndpoint => Owned && Endpoint.Length == 0
        ? $"http://localhost:{RestPort}"
        : Endpoint;
}
