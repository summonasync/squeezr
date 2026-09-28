using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

public class SemanticCoverageTests
{
    [Fact]
    public void Ratio_NoChunks_IsNull()
    {
        new SemanticCoverage(0, 0).Ratio.ShouldBeNull();
    }

    [Fact]
    public void Ratio_NothingPending_IsOne()
    {
        new SemanticCoverage(10, 0).Ratio.ShouldBe(1.0);
    }

    [Fact]
    public void Ratio_EverythingPending_IsZero()
    {
        new SemanticCoverage(7, 7).Ratio.ShouldBe(0.0);
    }

    [Fact]
    public void Ratio_Partial_FloorsToTwoDecimals()
    {
        new SemanticCoverage(4, 1).Ratio.ShouldBe(0.75);
        new SemanticCoverage(3, 2).Ratio.ShouldBe(0.33);
        new SemanticCoverage(12571, 3017).Ratio.ShouldBe(0.76);
    }

    [Fact]
    public void Ratio_SinglePendingChunk_NeverRoundsUpToFull()
    {
        new SemanticCoverage(10000, 1).Ratio.ShouldBe(0.99);
    }

    [Fact]
    public void Ratio_PendingAboveTotal_ClampsToZero()
    {
        new SemanticCoverage(2, 5).Ratio.ShouldBe(0.0);
    }
}
