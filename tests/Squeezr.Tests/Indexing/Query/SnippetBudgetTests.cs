using Shouldly;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Indexing.Query;

public class SnippetBudgetTests
{
    [Fact]
    public void PerHitTokens_NoBudget_ReturnsNull()
    {
        SnippetBudget.PerHitTokens(null, 8).ShouldBeNull();
        SnippetBudget.PerHitTokens(0, 8).ShouldBeNull();
    }

    [Fact]
    public void PerHitTokens_SplitsTheBudgetAcrossTopK()
    {
        SnippetBudget.PerHitTokens(2000, 8).ShouldBe(250);
    }

    [Fact]
    public void PerHitTokens_TinyBudget_FloorsAboveExactArmWorstCase()
    {
        SnippetBudget.PerHitTokens(30, 8).ShouldBe(SnippetBudget.MinPerHitTokens);
    }

    [Fact]
    public void PerHitTokens_NoTopK_UsesBreadthDivisor()
    {
        SnippetBudget.PerHitTokens(1600, null).ShouldBe(200);
    }

    [Fact]
    public void Floor_ExceedsWorstExactSnippetEstimate()
    {
        TokenCounter.Estimate(new string('x', 201)).ShouldBeLessThanOrEqualTo(SnippetBudget.MinPerHitTokens);
    }

    [Fact]
    public void Clip_WithinCap_IsIdentity()
    {
        var snippet = new string('x', 100);

        SnippetBudget.Clip(snippet, 64).ShouldBe(snippet);
    }

    [Fact]
    public void Clip_PastCap_MarkerAndHonestEstimate()
    {
        var clipped = SnippetBudget.Clip(new string('x', 1000), 64);

        clipped.Length.ShouldBeLessThanOrEqualTo(SnippetBudget.MinPerHitTokens * 40 / 11);
        clipped.ShouldEndWith("…");
        TokenCounter.Estimate(clipped).ShouldBeLessThanOrEqualTo(64);
    }
}
