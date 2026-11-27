using System.Text.Json.Serialization;
using Squeezr.Semantic.Embeddings;

namespace Squeezr.Semantic.Configuration;

public sealed record OpenAiCompatibleOptions
{
    public string Endpoint { get; init; } = "https://api.openai.com/v1";

    public string ApiKeyEnv { get; init; } = "SQUEEZR_API_KEY";

    public string Model { get; init; } = "text-embedding-3-large";

    public int Dimensions { get; init; } = 1024;

    public string? ModelProfile { get; init; }

    [JsonIgnore]
    public EmbeddingModelProfile? ResolvedProfile =>
        ModelProfile is null ? null : EmbeddingModelProfiles.Resolve(ModelProfile);
}
