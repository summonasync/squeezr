using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

// reset empties queries.jsonl, so it is the one writer whose effect a late append would silently reverse —
// it queues on the same lock append and scrub use. The seam runs where the lock is already held, which is
// exactly where an unlocked truncate used to interleave: pinned from inside the critical section, not by timing.
public class QueryTelemetryResetTests
{
    private const int MidResetLookbackMs = 250;
    private const int SettleTimeoutMs = 30_000;

    [Fact]
    public void Reset_AppendReleasedMidReset_ParksOnTheLockAndKeepsItsLine()
    {
        var workspace = TelemetryStore();
        var log = SqueezrPaths.QueriesLogPath(workspace);
        Record(workspace, "search", new { query = "Load" });
        Record(workspace, "map", null);

        var writerEntered = new ManualResetEventSlim(false);
        var lateWrite = new Thread(() =>
        {
            writerEntered.Set();
            QueryTelemetry.Append(workspace, Entry("late-append"));
        });

        var lockWasFreeMidReset = false;
        var linesMidReset = 0;
        var lateWriteLandedMidReset = false;
        var probed = false;

        var removed = QueryTelemetry.Reset(workspace, () =>
        {
            if (!probed)
            {
                probed = true;
                lateWrite.Start();
                writerEntered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
                lockWasFreeMidReset = WriterLockIsFree(workspace);
                linesMidReset = File.ReadAllLines(log).Length;
                lateWriteLandedMidReset = lateWrite.Join(MidResetLookbackMs);
            }
        });

        lateWrite.Join(SettleTimeoutMs).ShouldBeTrue();

        removed.ShouldBe(2);
        lockWasFreeMidReset.ShouldBeFalse();
        linesMidReset.ShouldBe(2);
        lateWriteLandedMidReset.ShouldBeFalse();
        QueryTelemetry.ReadAll(workspace).Select(e => e.Tool).ToList().ShouldBe(["late-append"]);
    }

    [Fact]
    public void Reset_WhenAWriterHoldsOnPastTheBudget_RefusesAndClearsNothing()
    {
        var workspace = TelemetryStore();
        var log = SqueezrPaths.QueriesLogPath(workspace);
        Record(workspace, "search", new { query = "Load" });
        var before = File.ReadAllBytes(log);

        using (var held = TelemetryWriteLock.TryAcquire(SqueezrPaths.StoreDir(workspace)))
        {
            held.ShouldNotBeNull();
            var refusal = Should.Throw<WorkspaceException>(() => QueryTelemetry.Reset(workspace));
            refusal.Code.ShouldBe(WorkspaceException.DefaultCode);
            refusal.Message.ShouldContain("refused");
            refusal.Message.ShouldContain("nothing was cleared");
        }

        File.ReadAllBytes(log).ShouldBe(before);
        QueryTelemetry.Reset(workspace).ShouldBe(1);
        QueryTelemetry.Count(workspace).ShouldBe(0);
    }

    [Fact]
    public void Reset_WithoutTelemetryFile_ReportsZeroAndCreatesNothing()
    {
        var workspace = TelemetryStore();

        QueryTelemetry.Reset(workspace).ShouldBe(0);

        File.Exists(SqueezrPaths.QueriesLogPath(workspace)).ShouldBeFalse();
    }

    private static bool WriterLockIsFree(string workspaceRoot)
    {
        var path = TelemetryWriteLock.LockPath(SqueezrPaths.StoreDir(workspaceRoot));
        try
        {
            using var probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void Record(string workspaceRoot, string tool, object? parameters) =>
        QueryTelemetry.Record(workspaceRoot, Guid.NewGuid(), tool, parameters, 120, false, 1, 5);

    private static QueryTelemetryEntry Entry(string tool) =>
        new(DateTime.UtcNow, Guid.NewGuid(), tool, null, 0, false, 0, 1, false, null);

    private static string TelemetryStore()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(workspace));
        return workspace;
    }
}
