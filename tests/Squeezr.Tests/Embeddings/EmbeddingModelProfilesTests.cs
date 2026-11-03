using Shouldly;
using Squeezr.Core.Embeddings;

namespace Squeezr.Tests.Embeddings;

public class EmbeddingModelProfilesTests
{
    [Fact]
    public void NomicEmbedTextV15_MatchesSpec()
    {
        var profile = EmbeddingModelProfiles.NomicEmbedTextV15;

        profile.Id.ShouldBe("nomic-embed-text-v1.5");
        profile.Dimensions.ShouldBe(768);
        profile.Pooling.ShouldBe(EmbeddingPooling.MeanL2);
        profile.DocumentPrefix.ShouldBe("search_document: ");
        profile.QueryPrefix.ShouldBe("search_query: ");
        profile.DefaultMaxSequenceLength.ShouldBe(512);
    }

    [Fact]
    public void BgeCodeV1_MatchesCurrentBehavior()
    {
        var profile = EmbeddingModelProfiles.BgeCodeV1;

        profile.Id.ShouldBe("bge-code-v1");
        profile.Dimensions.ShouldBe(1536);
        profile.Pooling.ShouldBe(EmbeddingPooling.LastToken);
        profile.DocumentPrefix.ShouldBeNull();
        profile.QueryPrefix.ShouldBeNull();
        profile.DefaultMaxSequenceLength.ShouldBe(512);
    }

    [Fact]
    public void Default_IsNomicEmbedTextV15()
    {
        EmbeddingModelProfiles.Default.ShouldBeSameAs(EmbeddingModelProfiles.NomicEmbedTextV15);
    }

    [Fact]
    public void Resolve_NomicId_ReturnsNomicProfile()
    {
        EmbeddingModelProfiles.Resolve("nomic-embed-text-v1.5")
            .ShouldBeSameAs(EmbeddingModelProfiles.NomicEmbedTextV15);
    }

    [Fact]
    public void Resolve_BgeId_ReturnsBgeProfile()
    {
        EmbeddingModelProfiles.Resolve("bge-code-v1").ShouldBeSameAs(EmbeddingModelProfiles.BgeCodeV1);
    }

    [Fact]
    public void Resolve_DiffersOnlyByCaseAndPadding_ReturnsProfile()
    {
        EmbeddingModelProfiles.Resolve("  BGE-CODE-V1 ").ShouldBeSameAs(EmbeddingModelProfiles.BgeCodeV1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_MissingName_ReturnsDefault(string? name)
    {
        EmbeddingModelProfiles.Resolve(name).ShouldBeSameAs(EmbeddingModelProfiles.Default);
    }

    [Fact]
    public void PrefixFor_NomicProfile_ReturnsUsageSpecificPrefixes()
    {
        var profile = EmbeddingModelProfiles.NomicEmbedTextV15;

        profile.PrefixFor(EmbeddingUsage.Document).ShouldBe("search_document: ");
        profile.PrefixFor(EmbeddingUsage.Query).ShouldBe("search_query: ");
    }

    [Fact]
    public void PrefixFor_BgeProfile_ReturnsNullForBothUsages()
    {
        var profile = EmbeddingModelProfiles.BgeCodeV1;

        profile.PrefixFor(EmbeddingUsage.Document).ShouldBeNull();
        profile.PrefixFor(EmbeddingUsage.Query).ShouldBeNull();
    }

    [Fact]
    public void Resolve_UnknownId_ThrowsListingKnownIds()
    {
        var error = Should.Throw<ArgumentException>(() => EmbeddingModelProfiles.Resolve("unknown-model"));

        error.Message.ShouldContain("'unknown-model'");
        error.Message.ShouldContain("nomic-embed-text-v1.5");
        error.Message.ShouldContain("bge-code-v1");
    }
}
