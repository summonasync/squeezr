using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Embeddings;

namespace Squeezr.Tests.Embeddings;

public class EmbeddingProviderLogLineTests
{
    [Fact]
    public void Describe_NullResolution_ReturnsNull()
    {
        EmbeddingProviderLogLine.Describe(null).ShouldBeNull();
    }

    [Fact]
    public void Describe_ExplicitProvider_NamesProviderWithoutReason()
    {
        EmbeddingProviderLogLine.Describe(new EmbeddingProviderResolution("ollama", null))
            .ShouldBe("embeddings provider: ollama");
    }

    [Fact]
    public void Describe_ResolutionWithReason_AppendsOllamaUnreachableReason()
    {
        EmbeddingProviderLogLine.Describe(new EmbeddingProviderResolution("ollama", "connection refused"))
            .ShouldBe("embeddings provider: ollama (ollama unreachable: connection refused)");
    }
}
