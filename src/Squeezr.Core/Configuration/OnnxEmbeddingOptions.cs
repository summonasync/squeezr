using System.Text.Json.Serialization;
using Squeezr.Core.Embeddings;

namespace Squeezr.Core.Configuration;

public sealed record OnnxEmbeddingOptions
{
    private static readonly string ModelsDir = Path.Combine("~", ".squeezr", "models");

    public string? Model { get; init; }

    public string ModelPath { get; init; } = Path.Combine(ModelsDir, "nomic-embed-text-v1.5.onnx");

    public string TokenizerPath { get; init; } = Path.Combine(ModelsDir, "nomic-embed-text-v1.5", "tokenizer.json");

    public int? Dimensions { get; init; }

    public int? MaxSequenceLength { get; init; }

    public int BatchSize { get; init; } = 32;

    [JsonIgnore]
    public EmbeddingModelProfile ResolvedProfile => EmbeddingModelProfiles.Resolve(Model);

    [JsonIgnore]
    public int EffectiveDimensions => Dimensions ?? ResolvedProfile.Dimensions;

    [JsonIgnore]
    public int EffectiveMaxSequenceLength => MaxSequenceLength ?? ResolvedProfile.DefaultMaxSequenceLength;
}
