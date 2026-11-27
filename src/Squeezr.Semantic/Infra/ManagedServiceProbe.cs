namespace Squeezr.Semantic.Infra;

public sealed record ManagedServiceProbe(string ServiceName, Uri Endpoint, string ProbePath)
{
    public static ManagedServiceProbe Ollama { get; } =
        new("ollama", new Uri(ServicesComposeFile.OllamaEndpoint), "/api/version");

    public static ManagedServiceProbe Qdrant { get; } =
        new("qdrant", new Uri(ServicesComposeFile.QdrantRestEndpoint), "/");

    public static IReadOnlyList<ManagedServiceProbe> All { get; } = [Ollama, Qdrant];
}
