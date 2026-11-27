using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squeezr.Core.Configuration;

public sealed record SqueezrConfig
{
    public required ChunkingConfig Chunking { get; init; }

    public required IndexingConfig Indexing { get; init; }

    public WatchConfig Watch { get; init; } = new();

    // Sections this assembly does not model (the semantic product's embeddings/qdrant, and any
    // hand-added section) ride here as raw JSON: Core carries them through a Load/Save round trip
    // without naming or validating them, and the semantic assembly binds them on read.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public static SqueezrConfig CreateDefault() =>
        new()
        {
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
                    "**/.mindlash/worktrees/**",
                    "**/.qwen/worktrees/**",
                    "**/*.min.js",
                    "**/*.min.mjs",
                    "**/*.min.cjs",
                    "**/*.tsbuildinfo",
                    "**/web/out/**",
                    "**/.qwen/tmp/**",
                    "**/coverage/**",
                    "**/.turbo/**",
                    "**/.pytest_cache/**",
                    "**/.gradle/**",
                    "**/TestResults/**"
                ],
                MaxFileSizeKb = 512,
                NoSemantic = false,
                RankExclude = []
            },
            Watch = new WatchConfig()
        };
}
