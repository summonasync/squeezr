using Shouldly;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Tests.Indexing.Watch.Supervision;

public sealed class CrashLoopGuardTests
{
    private static readonly DateTime Start = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FiveRestartsInsideTheWindow_AreAllowed_TheSixthIsNot()
    {
        var guard = new CrashLoopGuard();

        for (var attempt = 0; attempt < CrashLoopGuard.RestartLimit; attempt++)
        {
            guard.RegisterRestart("demo", Start.AddMinutes(attempt)).ShouldBeTrue();
        }

        guard.RegisterRestart("demo", Start.AddMinutes(5)).ShouldBeFalse();
        guard.IsWedged("demo").ShouldBeTrue();
    }

    [Fact]
    public void WedgedStore_StaysBlocked_EvenAfterTheWindowEmpties()
    {
        var guard = new CrashLoopGuard();
        for (var attempt = 0; attempt <= CrashLoopGuard.RestartLimit; attempt++)
        {
            guard.RegisterRestart("demo", Start.AddMinutes(attempt));
        }

        guard.RegisterRestart("demo", Start.AddMinutes(30)).ShouldBeFalse();
        guard.IsWedged("demo").ShouldBeTrue();
    }

    [Fact]
    public void RestartsBeyondTheWindow_DoNotCountTowardTheLimit()
    {
        var guard = new CrashLoopGuard();
        for (var attempt = 0; attempt < CrashLoopGuard.RestartLimit; attempt++)
        {
            guard.RegisterRestart("demo", Start.AddMinutes(attempt));
        }

        guard.RegisterRestart("demo", Start.AddMinutes(11)).ShouldBeTrue();
        guard.IsWedged("demo").ShouldBeFalse();
    }

    [Fact]
    public void WedgeIsPerStore_OtherStoresKeepRestarting()
    {
        var guard = new CrashLoopGuard();
        for (var attempt = 0; attempt <= CrashLoopGuard.RestartLimit; attempt++)
        {
            guard.RegisterRestart("demo", Start.AddMinutes(attempt));
        }

        guard.RegisterRestart("other", Start.AddMinutes(6)).ShouldBeTrue();
        guard.IsWedged("other").ShouldBeFalse();
    }

    [Fact]
    public void Reset_UnwedgesTheStore_AndStartsAWindowFromScratch()
    {
        var guard = new CrashLoopGuard();
        for (var attempt = 0; attempt <= CrashLoopGuard.RestartLimit; attempt++)
        {
            guard.RegisterRestart("demo", Start.AddMinutes(attempt));
        }

        guard.Reset("demo");

        guard.IsWedged("demo").ShouldBeFalse();
        guard.RegisterRestart("demo", Start.AddMinutes(6)).ShouldBeTrue();
    }
}
