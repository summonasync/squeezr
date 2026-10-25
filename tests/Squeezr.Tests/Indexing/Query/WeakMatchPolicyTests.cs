using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Indexing.Query;

public class WeakMatchPolicyTests
{
    [Fact]
    public void IsWeak_ZeroExactArm_LowTopScore_IsWeak()
    {
        WeakMatchPolicy.IsWeak(0, [0.704, 0.687, 0.662]).ShouldBeTrue();
    }

    [Fact]
    public void IsWeak_ZeroExactArm_TopScoreAboveFloor_NotWeak()
    {
        WeakMatchPolicy.IsWeak(0, [0.749, 0.746, 0.737]).ShouldBeFalse();
    }

    [Fact]
    public void IsWeak_ExactArmPresent_NeverWeak()
    {
        WeakMatchPolicy.IsWeak(1, [0.1]).ShouldBeFalse();
    }

    [Fact]
    public void IsWeak_TopScoreAtFloor_NotWeak()
    {
        WeakMatchPolicy.IsWeak(0, [WeakMatchPolicy.SemanticFloor]).ShouldBeFalse();
    }

    [Fact]
    public void IsWeak_NoSemanticHits_NotWeak()
    {
        WeakMatchPolicy.IsWeak(0, []).ShouldBeFalse();
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

        warning.ShouldContain("narrow with prefix");
        warning.ShouldNotContain("kind=");
    }
}
