using System.Runtime.Versioning;
using Shouldly;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Tests.Indexing.Watch.Supervision;

public sealed class CrashLoopGuardTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    private static readonly UnixFileMode DirWritable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly string _dir = TestDirs.NewTempDir();

    private string StatePath => Path.Combine(_dir, "watchd", "state.json");

    [Fact]
    public void FiveRestartsInsideTheWindow_AreAllowed_TheSixthIsNot()
    {
        var guard = NewGuard();

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
        var guard = NewGuard();
        Wedge(guard, "demo");

        guard.RegisterRestart("demo", Start.AddMinutes(30)).ShouldBeFalse();
        guard.IsWedged("demo").ShouldBeTrue();
    }

    [Fact]
    public void RestartsBeyondTheWindow_DoNotCountTowardTheLimit()
    {
        var guard = NewGuard();
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
        var guard = NewGuard();
        Wedge(guard, "demo");

        guard.RegisterRestart("other", Start.AddMinutes(6)).ShouldBeTrue();
        guard.IsWedged("other").ShouldBeFalse();
    }

    [Fact]
    public void Reset_UnwedgesTheStore_AndStartsAWindowFromScratch()
    {
        var guard = NewGuard();
        Wedge(guard, "demo");

        guard.Reset("demo");

        guard.IsWedged("demo").ShouldBeFalse();
        guard.RegisterRestart("demo", Start.AddMinutes(6)).ShouldBeTrue();
    }

    [Fact]
    public void TheSixthRestart_WritesTheWedgeMarkerIntoStateJson()
    {
        var guard = NewGuard();

        Wedge(guard, "demo");

        File.Exists(StatePath).ShouldBeTrue("the marker is the file, not the guard");
        new SupervisionState(StatePath).Read().IsWedged("demo").ShouldBeTrue();
        guard.WedgeWriteFault("demo").ShouldBeNull();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void WedgeWriteFails_MemoryKeepsTheBlock_AndTheSyncRefusesToReleaseWhatNeverFlushed()
    {
        LockTheChannel();
        try
        {
            var guard = NewGuard();
            Wedge(guard, "demo");

            guard.IsWedged("demo").ShouldBeTrue();
            guard.WedgeWriteFault("demo").ShouldNotBeNull("the write fault is the operator's only signal");
            new SupervisionState(StatePath).Read().IsWedged("demo")
                .ShouldBeFalse("the channel never accepted the marker");

            guard.SyncWedged(new HashSet<string>(StringComparer.Ordinal));

            guard.IsWedged("demo").ShouldBeTrue("the file cannot release a marker it never accepted");
            guard.RegisterRestart("demo", Start.AddMinutes(30)).ShouldBeFalse(
                "an unflushed wedge must not hand the store a fresh budget on the next sweep");
        }
        finally
        {
            UnlockTheChannel();
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void WedgeWriteFails_HealsOnTheNextSyncOnceTheChannelOpens_AndTheMarkerLands()
    {
        LockTheChannel();
        var guard = NewGuard();
        try
        {
            Wedge(guard, "demo");
            guard.WedgeWriteFault("demo").ShouldNotBeNull();
        }
        finally
        {
            UnlockTheChannel();
        }

        guard.SyncWedged(new HashSet<string>(StringComparer.Ordinal));

        new SupervisionState(StatePath).Read().IsWedged("demo").ShouldBeTrue();
        guard.WedgeWriteFault("demo").ShouldBeNull();
        guard.IsWedged("demo").ShouldBeTrue();
        guard.RegisterRestart("demo", Start.AddMinutes(30)).ShouldBeFalse();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void WedgeWriteFails_ACliResetStillReleasesTheStore_ForTheGuardThatCouldNotFlush()
    {
        LockTheChannel();
        try
        {
            var guard = NewGuard();
            Wedge(guard, "demo");

            guard.Reset("demo");

            guard.IsWedged("demo").ShouldBeFalse();
            guard.WedgeWriteFault("demo").ShouldBeNull();
            guard.RegisterRestart("demo", Start.AddMinutes(30)).ShouldBeTrue();
        }
        finally
        {
            UnlockTheChannel();
        }
    }

    [Fact]
    public void Wedge_SetByTheLiveProcess_IsSeenByAFreshGuardOverTheSameFile()
    {
        var watchd = NewGuard();
        Wedge(watchd, "demo");

        var cli = NewGuard();
        cli.SyncWedged(new SupervisionState(StatePath).Read().Wedged);

        cli.IsWedged("demo").ShouldBeTrue();
        cli.RegisterRestart("demo", Start.AddMinutes(30)).ShouldBeFalse();
    }

    [Fact]
    public void Reset_ByAFreshGuardOverTheSameFile_ClearsTheMarkerInTheFile()
    {
        var watchd = NewGuard();
        Wedge(watchd, "demo");

        NewGuard().Reset("demo");

        new SupervisionState(StatePath).Read().IsWedged("demo").ShouldBeFalse();
    }

    [Fact]
    public void CliSideReset_ReleasesTheStore_ForTheLiveWatchdSideGuard()
    {
        var watchd = NewGuard();
        Wedge(watchd, "demo");

        NewGuard().Reset("demo");
        watchd.SyncWedged(new SupervisionState(StatePath).Read().Wedged);

        watchd.IsWedged("demo").ShouldBeFalse();
        watchd.RegisterRestart("demo", Start.AddMinutes(30))
            .ShouldBeTrue("a store released from the file starts a fresh budget, not the inherited one");
    }

    [Fact]
    public void SyncWedged_AdoptsMarkersAnotherProcessAdded_AndSparesTheStoresTheFileDoesNotCarry()
    {
        var watchd = NewGuard();
        Wedge(watchd, "demo");
        var state = new SupervisionState(StatePath);
        state.SetWedged("other", true);

        watchd.SyncWedged(state.Read().Wedged);

        watchd.IsWedged("demo").ShouldBeTrue();
        watchd.IsWedged("other").ShouldBeTrue();
        watchd.IsWedged("third").ShouldBeFalse();
        watchd.RegisterRestart("third", Start).ShouldBeTrue();
    }

    [Fact]
    public void SyncWedged_LeavesDesiresOfOtherStoresUntouched()
    {
        var state = new SupervisionState(StatePath);
        state.SetDesire("demo", WatchDesire.Stopped);
        var guard = NewGuard();
        Wedge(guard, "other");

        guard.SyncWedged(state.Read().Wedged);

        new SupervisionState(StatePath).DesireOf("demo").ShouldBe(WatchDesire.Stopped);
    }

    private CrashLoopGuard NewGuard() => new(new SupervisionState(StatePath));

    [SupportedOSPlatform("linux")]
    private void LockTheChannel()
    {
        var dir = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(dir);
        new SupervisionState(StatePath).SetDesire("seed", WatchDesire.Running);
        File.SetUnixFileMode(dir, DirWritable & ~UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("linux")]
    private void UnlockTheChannel() =>
        File.SetUnixFileMode(Path.GetDirectoryName(StatePath)!, DirWritable);

    private static void Wedge(CrashLoopGuard guard, string storeKey)
    {
        for (var attempt = 0; attempt <= CrashLoopGuard.RestartLimit; attempt++)
        {
            guard.RegisterRestart(storeKey, Start.AddMinutes(attempt));
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
