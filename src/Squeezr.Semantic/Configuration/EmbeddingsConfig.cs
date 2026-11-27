namespace Squeezr.Semantic.Configuration;

public sealed record EmbeddingsConfig
{
    public string Provider { get; init; } = "auto";

    public OpenAiCompatibleOptions OpenAiCompatible { get; init; } = new();

    public OllamaOptions Ollama { get; init; } = new();
}
