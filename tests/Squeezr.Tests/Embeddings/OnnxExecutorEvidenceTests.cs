using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Embeddings;

namespace Squeezr.Tests.Embeddings;

public class OnnxExecutorEvidenceTests
{
    [Fact]
    public void Derive_CpuOnlyBuild_PinsCpuWithTheProvidersAsEvidence()
    {
        var executor = OnnxExecutorEvidence.Derive(["CPUExecutionProvider"]);

        executor.Device.ShouldBe("cpu");
        executor.Reason.ShouldContain("CPUExecutionProvider");
    }

    [Fact]
    public void Derive_GpuProviderAvailable_NeverPromotesItToTheDevice()
    {
        var executor = OnnxExecutorEvidence.Derive(["CUDAExecutionProvider", "CPUExecutionProvider"]);

        executor.Device.ShouldBe("cpu");
        executor.Reason.ShouldContain("CUDAExecutionProvider");
    }

    [Fact]
    public void Derive_NoProvidersReported_IsStillCpuWithANote()
    {
        var executor = OnnxExecutorEvidence.Derive([]);

        executor.Device.ShouldBe("cpu");
        executor.Reason.ShouldContain("none reported");
    }

    [Fact]
    public void ProbeExecutor_ReadsTheLiveOnnxRuntimeProviders()
    {
        var provider = new OnnxEmbeddingProvider(
            new SilentSession(), new SilentEncoder(), EmbeddingModelProfiles.NomicEmbedTextV15);

        var executor = provider.ProbeExecutor();

        executor.Device.ShouldBe("cpu");
        executor.Reason.ShouldNotBeNull();
    }

    private sealed class SilentSession : IOnnxSession
    {
        public IReadOnlyCollection<string> InputNames { get; } = [];

        public DenseTensor<float> Run(IReadOnlyCollection<NamedOnnxValue> inputs) =>
            throw new NotSupportedException("executor evidence never runs inference");

        public void Dispose()
        {
        }
    }

    private sealed class SilentEncoder : IEmbeddingTextEncoder
    {
        public int FallbackTokenId => 0;

        public IReadOnlyList<int> EncodeToIds(string text) => [];
    }
}
