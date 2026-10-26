using Shouldly;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Indexing.Query;

public class ScopedVectorBudgetTests
{
    [Fact]
    public void Ceiling_IsTheDeclaredWorstCase_AboveTheLegacyFixedOversample()
    {
        ScopedVectorBudget.Ceiling.ShouldBeGreaterThan(4 * ScopedVectorBudget.BaseTopK);
    }

    [Theory]
    [InlineData(8, 8, 20)]
    [InlineData(9, 8, 20)]
    [InlineData(4, 8, 40)]
    [InlineData(2, 8, 80)]
    [InlineData(1, 8, 160)]
    [InlineData(1, 16, 160)]
    [InlineData(0, 8, 160)]
    public void For_InScopeShare_ScalesBaseTopKByInverseShareCappedAtTheCeiling(
        int inScopeFiles, int universeFiles, int expected)
    {
        ScopedVectorBudget.For(inScopeFiles, universeFiles).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, 4, 80)]
    [InlineData(25, 100, 80)]
    [InlineData(50, 200, 80)]
    public void For_QuarterOfTheUniverse_ReproducesTheLegacyFixedOversample(
        int inScopeFiles, int universeFiles, int expected)
    {
        ScopedVectorBudget.For(inScopeFiles, universeFiles).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, 3, 60)]
    [InlineData(5, 17, 68)]
    [InlineData(37, 1000, 160)]
    [InlineData(3, 100000, 160)]
    public void For_FormulaIdentity_IsCeilOfBaseTopKTimesUniverseOverInScopeFilesCappedAtCeiling(
        int inScopeFiles, int universeFiles, int expected)
    {
        var raw = (int)Math.Ceiling(ScopedVectorBudget.BaseTopK * (double)universeFiles / inScopeFiles);
        Math.Min(ScopedVectorBudget.Ceiling, raw).ShouldBe(expected);

        ScopedVectorBudget.For(inScopeFiles, universeFiles).ShouldBe(expected);
    }

    [Fact]
    public void For_NarrowerScope_NeverAsksForFewerCandidates()
    {
        var previous = ScopedVectorBudget.For(1000, 1000);
        for (var inScope = 999; inScope >= 1; inScope--)
        {
            var current = ScopedVectorBudget.For(inScope, 1000);
            current.ShouldBeGreaterThanOrEqualTo(previous);
            previous = current;
        }
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(5, 17)]
    [InlineData(37, 1000)]
    [InlineData(3, 100000)]
    public void For_AnyNarrowScope_StaysWithinBaseTopKAndTheCeiling(int inScopeFiles, int universeFiles)
    {
        var topK = ScopedVectorBudget.For(inScopeFiles, universeFiles);

        topK.ShouldBeGreaterThanOrEqualTo(ScopedVectorBudget.BaseTopK);
        topK.ShouldBeLessThanOrEqualTo(ScopedVectorBudget.Ceiling);
    }
}
