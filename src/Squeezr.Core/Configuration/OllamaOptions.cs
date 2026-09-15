using System.Text.Json.Serialization;
using Squeezr.Core.Embeddings;

namespace Squeezr.Core.Configuration;

public sealed record OllamaOptions
{
    public string Endpoint { get; init; } = "http://localhost:11434";

    public string Model { get; init; } = "nomic-embed-text";

    public int Dimensions { get; init; } = 768;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ModelProfile { get; init; } = "nomic-embed-text-v1.5";

    [JsonIgnore]
    public EmbeddingModelProfile? ResolvedProfile =>
        ModelProfile is null ? null : EmbeddingModelProfiles.Resolve(ModelProfile);
}
