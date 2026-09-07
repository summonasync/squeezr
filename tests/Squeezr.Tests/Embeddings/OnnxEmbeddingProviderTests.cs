using Shouldly;
using Squeezr.Cli.Embeddings;

namespace Squeezr.Tests.Embeddings;

public class OnnxEmbeddingProviderTests
{
    private static (string ModelPath, string TokenizerDir)? FindBundledModel()
    {
        var modelsDir = Path.Combine(AppContext.BaseDirectory, "Models");
        var modelPath = Path.Combine(modelsDir, "bge-code-v1.onnx");
        var tokenizerDir = Path.Combine(modelsDir, "bge-code-v1");
        return File.Exists(modelPath) && File.Exists(Path.Combine(tokenizerDir, "vocab.json"))
            ? (modelPath, tokenizerDir)
            : null;
    }

    [Fact]
    public async Task Provider_ReturnsConfiguredDimensionsAndUnitVectors()
    {
        var bundled = FindBundledModel();
        if (bundled is null)
            return;

        var provider = new OnnxEmbeddingProvider(bundled.Value.ModelPath, bundled.Value.TokenizerDir, dimensions: 1536);

        provider.Dimensions.ShouldBe(1536);

        var vectors = await provider.EmbedAsync(["public class Foo { }", "def bar(): pass"], CancellationToken.None);

        vectors.Length.ShouldBe(2);
        vectors[0].Length.ShouldBe(1536);
        vectors[1].Length.ShouldBe(1536);
        Norm(vectors[0]).ShouldBe(1.0, 1e-4);
        Norm(vectors[1]).ShouldBe(1.0, 1e-4);
        vectors[0].ShouldNotBe(vectors[1]);
    }

    private static double Norm(float[] vector)
    {
        var sum = 0.0;
        foreach (var v in vector)
            sum += v * v;
        return Math.Sqrt(sum);
    }
}
