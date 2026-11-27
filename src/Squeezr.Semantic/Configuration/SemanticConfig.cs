using System.Text.Json;
using Squeezr.Core.Configuration;
using Squeezr.Core.Serialization;

namespace Squeezr.Semantic.Configuration;

public sealed record SemanticConfig(SqueezrConfig Core, EmbeddingsConfig Embeddings, QdrantConfig Qdrant)
{
    public static SemanticConfig Bind(SqueezrConfig core) =>
        new(core, Section<EmbeddingsConfig>(core, "embeddings") ?? new EmbeddingsConfig(),
            Section<QdrantConfig>(core, "qdrant") ?? new QdrantConfig());

    // Pass-throughs so editors can treat the bound view as the whole config: writing one routes into
    // the carried core document, so a later Attach saves the edited section with the rest.
    public ChunkingConfig Chunking { get => Core.Chunking; init => Core = Core with { Chunking = value }; }

    public IndexingConfig Indexing { get => Core.Indexing; init => Core = Core with { Indexing = value }; }

    public WatchConfig Watch { get => Core.Watch; init => Core = Core with { Watch = value }; }

    private static T? Section<T>(SqueezrConfig core, string name) where T : class
    {
        if (core.ExtensionData is not { } sections || !sections.TryGetValue(name, out var section))
        {
            return null;
        }

        return section.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : JsonSerializer.Deserialize<T>(section.GetRawText(), SqueezrJson.FileOptions);
    }
}
