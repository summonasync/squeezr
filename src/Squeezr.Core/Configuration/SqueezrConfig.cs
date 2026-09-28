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
                    "**/.next/**",
                    "**/.vs/**",
                    "**/.idea/**",
                    "**/.claude/worktrees/**",
                    "**/*.min.js",
                    "**/*.tsbuildinfo"
                ],
                MaxFileSizeKb = 512,
                NoSemantic = false,
                RankExclude = []
            },
            Qdrant = new QdrantConfig
            {
                Endpoint = "http://localhost:6334"
            },
            Watch = new WatchConfig()
        };
}
