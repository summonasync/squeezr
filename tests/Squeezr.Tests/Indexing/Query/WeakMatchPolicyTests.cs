using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Indexing.Query;

public class WeakMatchPolicyTests
{
    // the bge-code-v1 profile the product already ships and the TUI config editor already offers,
    // and an unregistered OpenAI-compatible name — both speak for a score distribution nobody measured
    private static readonly string BgeCodeKey = EmbeddingModelKey.From(new EmbeddingsConfig
    {
        Provider = "ollama",
        Ollama = new OllamaOptions { Model = "bge-code", Dimensions = 1536, ModelProfile = "bge-code-v1" }
    });

    private static readonly string UnknownOpenAiKey = EmbeddingModelKey.From(new EmbeddingsConfig
    {
        Provider = "openai",
        OpenAiCompatible = new OpenAiCompatibleOptions { Model = "squeezr-unknown-embed", Dimensions = 512 }
    });

    [Fact]
    public void CalibratedFloors_HoldOneEntry_AndTheShippedDefaultConfigResolvesToIt()
    {
        var defaultKey = EmbeddingModelKey.From(new EmbeddingsConfig { Provider = "ollama" });

        WeakMatchPolicy.CalibratedFloors.ShouldHaveSingleItem();
        defaultKey.ShouldBe("ollama/nomic-embed-text/768/nomic-embed-text-v1.5");
        WeakMatchPolicy.CalibratedModelKey.ShouldBe(defaultKey);
        WeakMatchPolicy.CalibratedFloors[defaultKey].ShouldBe(0.72);
    }

    [Fact]
    public void IsWeak_CalibratedKey_FarBelowFloor_IsWeak()
    {
        WeakMatchPolicy.IsWeak(0, [0.05], TestEmbeddingKeys.Calibrated).ShouldBeTrue();
    }

    [Fact]
    public void IsWeak_BgeCodeProfile_FarBelowFloor_NotWeak()
    {
        WeakMatchPolicy.HasCalibratedFloor(BgeCodeKey).ShouldBeFalse();
        WeakMatchPolicy.IsWeak(0, [0.31, 0.22], BgeCodeKey).ShouldBeFalse();
    }

    [Fact]
    public void IsWeak_UnregisteredOpenAiModelName_FarBelowFloor_NotWeak()
    {
        WeakMatchPolicy.HasCalibratedFloor(UnknownOpenAiKey).ShouldBeFalse();
        WeakMatchPolicy.IsWeak(0, [0.31, 0.22], UnknownOpenAiKey).ShouldBeFalse();
    }

    [Fact]
    public void IsWeak_NoModelKeyAtAll_NeverFires()
    {
        WeakMatchPolicy.HasCalibratedFloor(null).ShouldBeFalse();
        WeakMatchPolicy.IsWeak(0, [0.05], null).ShouldBeFalse();
    }

    [Fact]
    public void CalibrationHint_NamesTheActiveKeyAndMakesNoConfidenceClaim()
    {
        var hint = WeakMatchPolicy.CalibrationHint(BgeCodeKey);

        hint.ShouldBe(
            "weak-match floor uncalibrated for ollama/bge-code/1536/bge-code-v1 — no low-confidence claim made until a floor is measured for it");
        hint.ShouldNotContain("at low confidence");
    }

    [Fact]
    public void IsWeak_ZeroExactArm_LowTopScore_IsWeak()
    {
        WeakMatchPolicy.IsWeak(0, [0.704, 0.687, 0.662], TestEmbeddingKeys.Calibrated).ShouldBeTrue();
    }

    [Fact]
    public void IsWeak_ZeroExactArm_TopScoreAboveFloor_NotWeak()
    {
        WeakMatchPolicy.IsWeak(0, [0.749, 0.746, 0.737], TestEmbeddingKeys.Calibrated).ShouldBeFalse();
    }

    [Fact]
    public void IsWeak_ExactArmPresent_NeverWeak()
    {
        WeakMatchPolicy.IsWeak(1, [0.1], TestEmbeddingKeys.Calibrated).ShouldBeFalse();
    }

    [Fact]
    public void IsWeak_TopScoreAtFloor_NotWeak()
    {
        WeakMatchPolicy.IsWeak(
            0,
            [WeakMatchPolicy.CalibratedFloors[WeakMatchPolicy.CalibratedModelKey]],
            TestEmbeddingKeys.Calibrated).ShouldBeFalse();
    }

    [Fact]
    public void IsWeak_NoSemanticHits_NotWeak()
    {
        WeakMatchPolicy.IsWeak(0, [], TestEmbeddingKeys.Calibrated).ShouldBeFalse();
    }

    [Fact]
    public void Warning_PluralHits_NamesKindAndPrefix()
    {
        var warning = WeakMatchPolicy.Warning(5, null);

        warning.ShouldBe($"no exact-arm match; 5 semantic hits at low confidence — narrow with kind={DocKindDetector.Code} or prefix");
    }

    [Fact]
    public void Warning_SingleHit_Singularizes()
    {
        WeakMatchPolicy.Warning(1, null).ShouldContain("1 semantic hit at low confidence");
    }

    [Fact]
    public void Warning_AlreadyNarrowedToCode_SuggestsPrefixOnly()
    {
        var warning = WeakMatchPolicy.Warning(3, DocKindDetector.Code);

        warning.ShouldBe("no exact-arm match; 3 semantic hits at low confidence — narrow with prefix");
        warning.ShouldNotContain("kind=");
    }

    [Fact]
    public void Warning_DocsKind_NeverSuggestsSwitchingKind()
    {
        var warning = WeakMatchPolicy.Warning(4, DocKindDetector.Docs);

        warning.ShouldBe("no exact-arm match; 4 semantic hits at low confidence — narrow with prefix");
        warning.ShouldNotContain("kind=code");
    }

    [Fact]
    public void Warning_ExplicitAllKind_StillOffersTheKindMove()
    {
        var warning = WeakMatchPolicy.Warning(2, DocKindDetector.All);

        warning.ShouldContain("kind=code");
        warning.ShouldBe(WeakMatchPolicy.Warning(2, null));
    }
}
