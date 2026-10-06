namespace Squeezr.Cli.Embeddings;

internal static class EmbeddingProviderServices
{
    public static bool RequiresManagedService(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "ollama" => true,
        "auto" => true,
        "onnx" => false,
        "openai" => false,
        _ => false
    };
}
