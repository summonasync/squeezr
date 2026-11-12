using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests;

internal static class TestEmbeddingKeys
{
    // the one key WeakMatchPolicy holds a measured floor for: every stand that answers a hybrid
    // search claims it, so today's weak-match sentences keep meaning what they mean on the default config
    public const string Calibrated = WeakMatchPolicy.CalibratedModelKey;

    // the second shipped profile — TUI-selectable, 1536 dimensions, no measured floor at all
    public static readonly string BgeCodeV1 = EmbeddingModelKey.From(new EmbeddingsConfig
    {
        Provider = "ollama",
        Ollama = new OllamaOptions { Model = "bge-code-v1", Dimensions = 1536, ModelProfile = "bge-code-v1" }
    });

    // the shipped OpenAI-compatible defaults: a model name nobody measured a floor for
    public static readonly string UnregisteredOpenAiModel = EmbeddingModelKey.From(new EmbeddingsConfig
    {
        Provider = "openai"
    });

    // for stands no floor is ever asked of — indexing, watch and serve paths only
    public const string TestModel = "ollama/squeezr-tests/4";
}
