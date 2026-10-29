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

    private static string OwnerJson(int pid, string host, DateTime startedAtUtc) =>
        JsonSerializer.Serialize(
            new { pid, host, startedAtUtc },
            SqueezrJson.FileOptions);

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
