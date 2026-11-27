using System.Text.Json.Serialization;
using Squeezr.Semantic.Embeddings;

namespace Squeezr.Semantic.Configuration;

public sealed record OllamaOptions
{
    public string Endpoint { get; init; } = "http://127.0.0.1:11434";

    public string Model { get; init; } = "nomic-embed-text";

    public int Dimensions { get; init; } = 768;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ModelProfile { get; init; } = "nomic-embed-text-v1.5";

    [JsonIgnore]
    public EmbeddingModelProfile? ResolvedProfile =>
        ModelProfile is null ? null : EmbeddingModelProfiles.Resolve(ModelProfile);
}
