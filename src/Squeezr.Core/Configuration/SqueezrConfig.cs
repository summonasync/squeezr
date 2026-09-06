namespace Squeezr.Core.Configuration;

public sealed record SqueezrConfig
{
    public required EmbeddingsConfig Embeddings { get; init; }

    public required ChunkingConfig Chunking { get; init; }

    public required IndexingConfig Indexing { get; init; }

    public required QdrantConfig Qdrant { get; init; }

    public static SqueezrConfig CreateDefault() =>
        new()
        {
            Embeddings = new EmbeddingsConfig
            {
                Provider = "onnx",
                Onnx = new OnnxEmbeddingOptions
                {
                    ModelPath = "~/.squeezr/models/bge-code-v1.onnx",
                    TokenizerPath = "~/.squeezr/models/bge-code-v1/tokenizer.json",
                    Dimensions = 768,
                    MaxSequenceLength = 512,
                    BatchSize = 32
                },
                OpenAiCompatible = new OpenAiCompatibleOptions
                {
                    Endpoint = "https://api.openai.com/v1",
                    ApiKeyEnv = "OPENAI_API_KEY",
                    Model = "text-embedding-3-large",
                    Dimensions = 1024
                }
            },
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
                    "**/*.min.js"
                ],
                MaxFileSizeKb = 512,
                GitAware = true,
                NoSemantic = false
            },
            Qdrant = new QdrantConfig
            {
                Endpoint = "http://localhost:6334"
            }
        };
}
