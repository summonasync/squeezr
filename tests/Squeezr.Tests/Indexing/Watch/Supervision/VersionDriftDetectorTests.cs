using Shouldly;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Tests.Indexing.Watch.Supervision;

public sealed class VersionDriftDetectorTests
{
    private static readonly DateTime ProcessStart = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BinaryNewerThanTheProcessStart_IsDrifted()
    {
        VersionDriftDetector.IsDrifted(ProcessStart, ProcessStart.AddMinutes(1)).ShouldBeTrue();
    }

    [Fact]
    public void BinaryOlderThanTheProcessStart_IsNotDrifted()
    {
        VersionDriftDetector.IsDrifted(ProcessStart, ProcessStart.AddMinutes(-1)).ShouldBeFalse();
    }

    [Fact]
    public void SameTimestamp_IsNotDrifted()
    {
        VersionDriftDetector.IsDrifted(ProcessStart, ProcessStart).ShouldBeFalse();
    }
}
