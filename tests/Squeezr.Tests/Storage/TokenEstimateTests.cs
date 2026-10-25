using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

public class TokenEstimateTests
{
    [Fact]
    public void WholeFile_ZeroBytes_StillCountsOne()
    {
        TokenEstimate.WholeFile(0).ShouldBe(1);
    }

    [Fact]
    public void WholeFile_RoundsUp_WithFactors()
    {
        TokenEstimate.WholeFile(41).ShouldBe(12);
        TokenEstimate.WholeFile(960).ShouldBe(264);
        TokenEstimate.WholeFile(4_000_000).ShouldBe(1_100_000);
    }

    [Fact]
    public void WholeFile_NeverBelowRawQuarters()
    {
        TokenEstimate.WholeFile(41).ShouldBeGreaterThanOrEqualTo(41 / 4);
        TokenEstimate.WholeFile(960).ShouldBeGreaterThanOrEqualTo(960 / 4);
    }
}
