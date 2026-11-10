using Shouldly;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Tests.Indexing.Watch.Supervision;

public sealed class OwnBinaryDriftTests
{
    private static readonly DateTime Started = new(2026, 10, 29, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Replaced = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Reason_NamesTheRetirementTheBinaryAndBothStamps()
    {
        var drift = new OwnBinaryDrift("/opt/squeezr/squeezr", Started, Replaced);

        var reason = drift.Reason;

        reason.ShouldStartWith("watchd is retiring after an upgrade: ");
        reason.ShouldContain("/opt/squeezr/squeezr");
        reason.ShouldContain("changed at 2026-10-29 12:00:00.000Z");
        reason.ShouldContain("this process started at 2026-10-29 11:00:00.000Z");
    }
}
