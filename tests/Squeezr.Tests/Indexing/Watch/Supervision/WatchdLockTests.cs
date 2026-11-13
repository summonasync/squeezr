using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Indexing.Watch.Supervision;

public sealed class WatchdLockTests : IDisposable
{
    private readonly string _dir = TestDirs.NewTempDir();

    private string LockPath => Path.Combine(_dir, "watchd", "watchd.lock");

    [Fact]
    public void FirstAcquire_Wins_AndSecondRefusesWithTheHolderPid()
    {
        using var first = new WatchdLock(LockPath);
        first.TryAcquire().Acquired.ShouldBeTrue();
        File.Exists(LockPath).ShouldBeTrue();

        using var second = new WatchdLock(LockPath);
        var outcome = second.TryAcquire();

        outcome.Acquired.ShouldBeFalse();
        outcome.OwnerPid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public void Release_DeletesTheLockFile_AndFreesTheNextAcquire()
    {
        var first = new WatchdLock(LockPath);
        first.TryAcquire().Acquired.ShouldBeTrue();
        first.Dispose();

        File.Exists(LockPath).ShouldBeFalse();
        using var second = new WatchdLock(LockPath);
        second.TryAcquire().Acquired.ShouldBeTrue();
    }

    [Fact]
    public void LockLeftByADeadHolder_DoesNotBlock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        File.WriteAllText(LockPath, OwnerJson(int.MaxValue, Environment.MachineName, DateTime.UtcNow));

        using var watchdLock = new WatchdLock(LockPath);
        watchdLock.TryAcquire().Acquired.ShouldBeTrue();
    }

    [Fact]
    public void LockWhosePidWasReused_DoesNotBlock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        File.WriteAllText(LockPath, OwnerJson(Environment.ProcessId, Environment.MachineName, DateTime.UtcNow.AddHours(-3)));

        using var watchdLock = new WatchdLock(LockPath, isProcessAlive: _ => true, processStartUtc: _ => DateTime.UtcNow);
        watchdLock.TryAcquire().Acquired.ShouldBeTrue();
    }

    [Fact]
    public void LockHeldOnAnotherHost_Refuses()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        File.WriteAllText(LockPath, OwnerJson(4242, "elsewhere", DateTime.UtcNow));

        using var watchdLock = new WatchdLock(LockPath);
        var outcome = watchdLock.TryAcquire();

        outcome.Acquired.ShouldBeFalse();
        outcome.OwnerPid.ShouldBe(4242);
        File.Exists(LockPath).ShouldBeTrue();
    }

    [Fact]
    public void UnparseableLockFile_DoesNotBlock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        File.WriteAllText(LockPath, "{ not json at all");

        using var watchdLock = new WatchdLock(LockPath);
        watchdLock.TryAcquire().Acquired.ShouldBeTrue();
    }

    [Fact]
    public void Dispose_AfterARefusedAcquire_LeavesTheHoldersLockAlone()
    {
        using var holder = new WatchdLock(LockPath);
        holder.TryAcquire().Acquired.ShouldBeTrue();

        var refused = new WatchdLock(LockPath);
        refused.TryAcquire().Acquired.ShouldBeFalse();
        refused.Dispose();

        File.Exists(LockPath).ShouldBeTrue();
    }

    [Fact]
    public async Task ConcurrentAcquiresOnAStaleLock_LeaveExactlyOneHolder()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        File.WriteAllText(LockPath, OwnerJson(int.MaxValue, Environment.MachineName, DateTime.UtcNow));
        using var first = new WatchdLock(LockPath);
        using var second = new WatchdLock(LockPath);
        var gate = new Barrier(2);

        var outcomes = await Task.WhenAll(
            Task.Run(() => Contend(first)),
            Task.Run(() => Contend(second)));

        outcomes.Count(outcome => outcome.Acquired).ShouldBe(1);
        File.Exists(LockPath).ShouldBeTrue();

        using var third = new WatchdLock(LockPath);
        third.TryAcquire().Acquired.ShouldBeFalse();

        WatchdLockOutcome Contend(WatchdLock contender)
        {
            gate.SignalAndWait();
            return contender.TryAcquire();
        }
    }

    [Fact]
    public void StealWithAMismatchedSnapshot_RestoresTheFileOnDisk_AndRefuses()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        var liveContent = OwnerJson(Environment.ProcessId, Environment.MachineName, DateTime.UtcNow);
        File.WriteAllText(LockPath, liveContent);

        using var contender = new WatchdLock(LockPath);
        contender
            .TryStealStaleFile(OwnerJson(int.MaxValue, Environment.MachineName, DateTime.UtcNow))
            .ShouldBeFalse();

        File.ReadAllText(LockPath).ShouldBe(liveContent);
        Directory.GetFiles(Path.GetDirectoryName(LockPath)!).ShouldHaveSingleItem().ShouldBe(LockPath);

        using var holder = new WatchdLock(LockPath, isProcessAlive: _ => true, processStartUtc: _ => DateTime.UtcNow);
        holder.TryAcquire().Acquired.ShouldBeFalse();
    }

    [Fact]
    public void StealWithTheExactSnapshot_MovesTheStaleFileAside_LeavingNoScraps()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        var staleContent = OwnerJson(int.MaxValue, Environment.MachineName, DateTime.UtcNow);
        File.WriteAllText(LockPath, staleContent);

        using var contender = new WatchdLock(LockPath);
        contender.TryStealStaleFile(staleContent).ShouldBeTrue();

        File.Exists(LockPath).ShouldBeFalse();
        Directory.GetFiles(Path.GetDirectoryName(LockPath)!).ShouldBeEmpty();
    }

    [Fact]
    public void UnreadableLockFile_RefusesInsteadOfStealing()
    {
        Directory.CreateDirectory(LockPath);

        using var contender = new WatchdLock(LockPath);
        var outcome = contender.TryAcquire();

        outcome.Acquired.ShouldBeFalse();
        outcome.OwnerPid.ShouldBeNull();
        Directory.Exists(LockPath).ShouldBeTrue();
    }

    [Fact]
    public void OwnStartUnreadable_ClaimOmitsTheStartTimeField_InsteadOfInventingNow()
    {
        using var holder = new WatchdLock(LockPath, processStartUtc: _ => null);
        holder.TryAcquire().Acquired.ShouldBeTrue();

        var claim = File.ReadAllText(LockPath);
        claim.ShouldNotContain("startedAtUtc");
        WatchdLockOwner.TryRead(LockPath)!.StartedAtUtc.ShouldBeNull();
    }

    [Fact]
    public void ClaimWithoutAStartTime_IsNotReadAsDrift_TheNextAcquireRefuses()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        File.WriteAllText(LockPath, OwnerJsonWithoutStart(Environment.ProcessId, Environment.MachineName));

        using var contender = new WatchdLock(
            LockPath,
            isProcessAlive: _ => true,
            processStartUtc: _ => new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc));
        var outcome = contender.TryAcquire();

        outcome.Acquired.ShouldBeFalse();
        outcome.OwnerPid.ShouldBe(Environment.ProcessId);
        File.ReadAllText(LockPath).ShouldBe(OwnerJsonWithoutStart(Environment.ProcessId, Environment.MachineName));
    }

    [Fact]
    public void LockWrittenByTheHolder_CarriesThePinnedWireShape()
    {
        var startedAt = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        using var holder = new WatchdLock(LockPath, processStartUtc: _ => startedAt);
        holder.TryAcquire().Acquired.ShouldBeTrue();

        File.ReadAllText(LockPath).ShouldBe(
            $$"""
              {
                "pid": {{Environment.ProcessId}},
                "host": "{{Environment.MachineName}}",
                "startedAtUtc": "2026-11-12T10:00:00Z"
              }
              """);
    }

    // The two silences are not one answer: a claim that recorded no start answers no drift question, while a
    // claim that recorded one on a process this machine cannot inspect has nothing left to stand on.
    [Theory]
    [InlineData(true, true, WatchdLockOwner.StartCheck.Agreed)]
    [InlineData(true, false, WatchdLockOwner.StartCheck.LiveUnreadable)]
    [InlineData(false, true, WatchdLockOwner.StartCheck.NotRecorded)]
    [InlineData(false, false, WatchdLockOwner.StartCheck.NotRecorded)]
    public void CheckAgainstLiveStart_KeepsTheTwoSilencesApart(bool recorded, bool readable, WatchdLockOwner.StartCheck expected)
    {
        var owner = new WatchdLockOwner(
            4242,
            Environment.MachineName,
            recorded ? new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc) : null);
        var live = readable
            ? recorded ? new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc) : new DateTime(2026, 11, 12, 13, 0, 0, DateTimeKind.Utc)
            : (DateTime?)null;

        owner.CheckAgainstLiveStart(live).ShouldBe(expected);
    }

    [Fact]
    public void CheckAgainstLiveStart_AClaimRecordedButNoLongerReadable_IsNotDrift()
    {
        var owner = new WatchdLockOwner(
            4242,
            Environment.MachineName,
            new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc));

        owner.CheckAgainstLiveStart(null).ShouldBe(WatchdLockOwner.StartCheck.LiveUnreadable);
    }

    [Fact]
    public void CheckAgainstLiveStart_ASecondApart_IsTheSameProcess()
    {
        var owner = new WatchdLockOwner(
            4242,
            Environment.MachineName,
            new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc));

        owner.CheckAgainstLiveStart(new DateTime(2026, 11, 12, 10, 0, 1, 500, DateTimeKind.Utc))
            .ShouldBe(WatchdLockOwner.StartCheck.Agreed);
    }

    // The edge the comparison actually keeps: the code reads `duration <= StartTimeTolerance`, so two
    // seconds on the dot still agrees and only the step past it drifts. The skew is a duration, so a
    // live start earlier than the record is measured the same way as one later.
    [Theory]
    [InlineData(1.9, WatchdLockOwner.StartCheck.Agreed)]
    [InlineData(2.0, WatchdLockOwner.StartCheck.Agreed)]
    [InlineData(-2.0, WatchdLockOwner.StartCheck.Agreed)]
    [InlineData(2.1, WatchdLockOwner.StartCheck.Drifted)]
    [InlineData(-2.1, WatchdLockOwner.StartCheck.Drifted)]
    public void CheckAgainstLiveStart_TheToleranceEdge_IsTwoSecondsInclusive(double skewSeconds, WatchdLockOwner.StartCheck expected)
    {
        var recorded = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        var owner = new WatchdLockOwner(4242, Environment.MachineName, recorded);

        owner.CheckAgainstLiveStart(recorded.AddSeconds(skewSeconds)).ShouldBe(expected);
    }

    private static string OwnerJsonWithoutStart(int pid, string host) =>
        $$"""
          {
            "pid": {{pid}},
            "host": "{{host}}"
          }
          """;

    private static string OwnerJson(int pid, string host, DateTime startedAtUtc) =>
        JsonSerializer.Serialize(
            new { pid, host, startedAtUtc },
            SqueezrJson.FileOptions);

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
