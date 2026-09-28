using Shouldly;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

public class QdrantStatusDriftTests
{
    [Theory]
    [InlineData(7492, 6618, 874)]
    [InlineData(117, 100, 17)]
    public void ExcessPoints_AboveTheTolerance_ReportsTheGap(long points, int chunks, long expected)
    {
        new QdrantStatus(true, points, 0, chunks).ExcessPoints.ShouldBe(expected);
    }

    [Theory]
    [InlineData(116, 100)]
    [InlineData(100, 100)]
    [InlineData(42, 100)]
    public void ExcessPoints_WithinTheTolerance_StaysQuiet(long points, int chunks)
    {
        new QdrantStatus(true, points, 0, chunks).ExcessPoints.ShouldBeNull();
    }

    [Fact]
    public void ExcessPoints_StoreUnavailable_HasNoPointsToJudge()
    {
        new QdrantStatus(false, null, 0, 100).ExcessPoints.ShouldBeNull();
    }
}
