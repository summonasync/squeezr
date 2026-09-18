namespace Squeezr.Core.Configuration;

public sealed record SqueezrConfig
{
    public EmbeddingsConfig Embeddings { get; init; } = new();

    public required ChunkingConfig Chunking { get; init; }

    public required IndexingConfig Indexing { get; init; }

    public required QdrantConfig Qdrant { get; init; }

    public WatchConfig Watch { get; init; } = new();

    public static SqueezrConfig CreateDefault() =>
        new()
        {
            Embeddings = new EmbeddingsConfig(),
            Chunking = new ChunkingConfig
            {
                MaxTokensPerChunk = 512,
                FallbackWindowLines = 60,
                FallbackOverlapLines = 15
            },
            Indexing = new IndexingConfig
            {
                Ignore =
                [
                    "**/node_modules/**",
                    "**/bin/**",
                    "**/obj/**",
                    "**/dist/**",
                    "**/.vs/**",
                    "**/.idea/**",
                    "**/*.min.js"
                ],
                MaxFileSizeKb = 512,
                NoSemantic = false
            },
            Qdrant = new QdrantConfig
            {
                Endpoint = "http://localhost:6334"
            },
            Watch = new WatchConfig()
        };
}
