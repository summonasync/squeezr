namespace Squeezr.Semantic.Embeddings;

public static class EmbeddingProviderServices
{
    public static bool RequiresManagedService(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "ollama" => true,
        "auto" => true,
        "openai" => false,
        _ => false
    };
}
