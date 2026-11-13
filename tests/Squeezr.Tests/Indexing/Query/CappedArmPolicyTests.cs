using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Indexing.Query;

public class CappedArmPolicyTests
{
    private static SemanticArm Arm(int requestedTopK, int storeCandidates, int inScope, int hits)
    {
        var matches = Enumerable.Range(0, hits)
            .Select(i => (Key: $"f{i}:1", Match: new SearchMatch("demo", $"f{i}.cs", 1, 0.8, "s") { Source = "semantic" }))
            .ToList();
        return new SemanticArm(matches, requestedTopK, storeCandidates, inScope);
    }

    [Fact]
    public void IsCapped_OversaturatedAskUnderBaseFill_IsCapped()
    {
        CappedArmPolicy.IsCapped(Arm(160, 160, 3, 2)).ShouldBeTrue();
    }

    [Fact]
    public void IsCapped_UnderCeilingAskStillSaturated_IsCapped()
    {
        CappedArmPolicy.IsCapped(Arm(34, 34, 2, 1)).ShouldBeTrue();
    }

    [Fact]
    public void IsCapped_BaseTopKRequest_NeverCapped()
    {
        CappedArmPolicy.IsCapped(Arm(ScopedVectorBudget.BaseTopK, ScopedVectorBudget.BaseTopK, 4, 3)).ShouldBeFalse();
    }

    [Fact]
    public void IsCapped_EmptyArm_NeverCapped()
    {
        CappedArmPolicy.IsCapped(SemanticArm.Empty).ShouldBeFalse();
    }

    [Fact]
    public void IsCapped_StoreUnderTheAsk_WholePoolWasExamined_NotCapped()
    {
        CappedArmPolicy.IsCapped(Arm(160, 40, 3, 2)).ShouldBeFalse();
    }

    [Fact]
    public void IsCapped_FullArmFill_NotCapped_CountersAreTruncatedPastTheBreak()
    {
        CappedArmPolicy.IsCapped(Arm(160, 160, 20, ScopedVectorBudget.BaseTopK)).ShouldBeFalse();
    }

    [Fact]
    public void IsCapped_ZeroInScope_StillCaps_SuppressionLivesInWarning()
    {
        CappedArmPolicy.IsCapped(Arm(160, 160, 0, 0)).ShouldBeTrue();
    }

    [Fact]
    public void Warning_NotCapped_ReturnsNull()
    {
        CappedArmPolicy.Warning(Arm(20, 20, 4, 3), exactArmHits: 0, null, null, null, pathGlob: null).ShouldBeNull();
    }

    [Fact]
    public void Warning_KindlessQuery_OffersKindPrefixAndRepo()
    {
        var warning = CappedArmPolicy.Warning(Arm(160, 160, 3, 2), exactArmHits: 0, null, null, null, pathGlob: null);

        warning.ShouldBe(
            "semantic arm capped at 160 examined candidates; 3 in scope, 2 delivered — narrow with --path-glob, kind=code, prefix or repo scope");
    }

    [Fact]
    public void Warning_ExplicitAllKind_StillOffersTheKindMove()
    {
        var warning = CappedArmPolicy.Warning(Arm(160, 160, 3, 2), exactArmHits: 0, null, null, DocKindDetector.All, pathGlob: null);

        warning.ShouldNotBeNull().ShouldContain("kind=code");
    }

    [Fact]
    public void Warning_CodeNarrowedQuery_SwitchesToPrefixAndRepo()
    {
        var warning = CappedArmPolicy.Warning(Arm(50, 50, 2, 1), exactArmHits: 0, null, "src/", DocKindDetector.Code, pathGlob: null);

        warning.ShouldNotBeNull().ShouldBe(
            "semantic arm capped at 50 examined candidates; 2 in scope, 1 delivered — narrow with --path-glob, narrower prefix or repo scope");
        warning.ShouldNotContain("kind=");
    }

    [Fact]
    public void Warning_DocsKind_NeverSuggestsSwitchingKind()
    {
        var warning = CappedArmPolicy.Warning(Arm(50, 50, 2, 1), exactArmHits: 0, null, null, DocKindDetector.Docs, pathGlob: null);

        warning.ShouldNotBeNull().ShouldContain("narrow with --path-glob, prefix or repo scope");
        warning.ShouldNotContain("kind=");
    }

    [Fact]
    public void Warning_RepoSet_DropsTheRepoMove()
    {
        var warning = CappedArmPolicy.Warning(Arm(50, 50, 2, 1), exactArmHits: 0, "demo", null, null, pathGlob: null);

        warning.ShouldNotBeNull().ShouldBe(
            "semantic arm capped at 50 examined candidates; 2 in scope, 1 delivered — narrow with --path-glob, kind=code or prefix");
        warning.ShouldNotContain("repo scope");
    }

    [Fact]
    public void Warning_FullyNarrowedQuery_AlwaysKeepsAPrefixMove()
    {
        var warning = CappedArmPolicy.Warning(Arm(50, 50, 2, 1), exactArmHits: 0, "demo", "src/", DocKindDetector.Code, pathGlob: null);

        warning.ShouldNotBeNull().ShouldBe(
            "semantic arm capped at 50 examined candidates; 2 in scope, 1 delivered — narrow with --path-glob or narrower prefix");
    }

    [Fact]
    public void Warning_GlobNarrowableUniverse_NamesPathGlob()
    {
        var warning = CappedArmPolicy.Warning(Arm(160, 160, 3, 2), exactArmHits: 0, null, null, null, pathGlob: null);

        warning.ShouldNotBeNull().ShouldContain("narrow with --path-glob");
    }

    [Fact]
    public void Warning_AlreadyGlobNarrowed_StaysQuietAboutGlob()
    {
        var warning = CappedArmPolicy.Warning(Arm(160, 160, 3, 2), exactArmHits: 0, null, null, null, pathGlob: "*.cs");

        warning.ShouldNotBeNull().ShouldNotContain("--path-glob");
        warning.ShouldBe(
            "semantic arm capped at 160 examined candidates; 3 in scope, 2 delivered — narrow with kind=code, prefix or repo scope");
    }

    [Fact]
    public void Warning_FullyServedScope_OversaturatedAsk_ReturnsNull()
    {
        var arm = Arm(160, 160, 1, 1);
        CappedArmPolicy.IsCapped(arm).ShouldBeTrue();

        var warning = CappedArmPolicy.Warning(arm, exactArmHits: 0, null, null, null, pathGlob: null);

        warning.ShouldBeNull();
    }

    [Fact]
    public void Warning_ExactArmAnswered_SemanticArmEmptyInScope_ReturnsNull()
    {
        var arm = Arm(160, 160, 3, 0);
        CappedArmPolicy.IsCapped(arm).ShouldBeTrue();

        var warning = CappedArmPolicy.Warning(arm, exactArmHits: 2, null, null, null, pathGlob: null);

        warning.ShouldBeNull();
    }

    [Fact]
    public void Warning_ExactArmEmpty_SemanticArmEmptyInScope_StillWarns()
    {
        var warning = CappedArmPolicy.Warning(Arm(160, 160, 3, 0), exactArmHits: 0, null, null, null, pathGlob: null);

        warning.ShouldBe(
            "semantic arm capped at 160 examined candidates; 3 in scope, 0 delivered — narrow with --path-glob, kind=code, prefix or repo scope");
    }
}
