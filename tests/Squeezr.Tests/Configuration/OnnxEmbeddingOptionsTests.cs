using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;

namespace Squeezr.Tests.Configuration;

public class OnnxEmbeddingOptionsTests
{
    [Fact]
    public void Defaults_ResolveToNomicProfile()
    {
        var options = new OnnxEmbeddingOptions();

        options.Model.ShouldBeNull();
        options.ResolvedProfile.ShouldBeSameAs(EmbeddingModelProfiles.NomicEmbedTextV15);
        options.EffectiveDimensions.ShouldBe(768);
        options.EffectiveMaxSequenceLength.ShouldBe(512);
    }

    [Fact]
    public void BgeModel_ResolvesToBgeProfile()
    {
        var options = new OnnxEmbeddingOptions { Model = "bge-code-v1" };

        options.ResolvedProfile.ShouldBeSameAs(EmbeddingModelProfiles.BgeCodeV1);
        options.EffectiveDimensions.ShouldBe(1536);
        options.EffectiveMaxSequenceLength.ShouldBe(512);
    }

    [Fact]
    public void ExplicitDimensions_OverrideProfileDimensions()
    {
        var options = new OnnxEmbeddingOptions { Model = "bge-code-v1", Dimensions = 256 };

        options.EffectiveDimensions.ShouldBe(256);
    }

    [Fact]
    public void ExplicitMaxSequenceLength_OverridesProfileDefault()
    {
        var options = new OnnxEmbeddingOptions { MaxSequenceLength = 128 };

        options.EffectiveMaxSequenceLength.ShouldBe(128);
    }

    [Fact]
    public void UnknownModel_ThrowsListingKnownIds()
    {
        var options = new OnnxEmbeddingOptions { Model = "no-such-model" };

        var error = Should.Throw<ArgumentException>(() => options.ResolvedProfile);

        error.Message.ShouldContain("'no-such-model'");
        error.Message.ShouldContain("nomic-embed-text-v1.5");
        error.Message.ShouldContain("bge-code-v1");
    }
}
