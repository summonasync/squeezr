using System.Diagnostics;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Storage;

// queries.jsonl takes a line from every cli read and every mcp tool call while a compact pass rewrites the
// whole file underneath them. The discipline is one writer at a time, and these pins force the interleaving
// from inside the scrub rather than hoping a race shows up: the classifier runs between the snapshot read and
// the move, so that is where a concurrent writer is released and where exclusivity is checked.
public class QueryTelemetryWriteDisciplineTests
{
    private const string EnvPath = ".env";
    private const int SeededLines = 1200;
    private const int ReadsBeforeTheSwap = 40;
    private const int Writers = 8;
    private const int LinesPerWriter = 20;
    private const int MidScrubLookbackMs = 250;
    private const int SettleTimeoutMs = 30_000;

    [Fact]
    public void Redact_AppendStartedMidScrub_IsHeldBackAndKeepsItsLine()
    {
        var workspace = TelemetryStore();
        var log = SqueezrPaths.QueriesLogPath(workspace);
        Record(workspace, "expand", new { file = EnvPath, startLine = 4 });
        Record(workspace, "search", new { query = "Load" });

        var writerEntered = new ManualResetEventSlim(false);
        var lateWrite = new Thread(() =>
        {
            writerEntered.Set();
            QueryTelemetry.Append(workspace, Entry("repo_map"));
        });

        var lockWasFreeMidScrub = false;
        var linesMidScrub = 0;
        var lateWriteLandedMidScrub = false;
        var probed = false;

        var redacted = QueryTelemetry.RedactProtectedParams(workspace, path =>
        {
            if (!probed)
            {
                probed = true;
                lateWrite.Start();
                writerEntered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
                lockWasFreeMidScrub = WriterLockIsFree(workspace);
                linesMidScrub = File.ReadAllLines(log).Length;
                lateWriteLandedMidScrub = lateWrite.Join(MidScrubLookbackMs);
            }

            return BuiltInsOnly(path);
        });

        lateWrite.Join(SettleTimeoutMs).ShouldBeTrue();

        redacted.ShouldBe(1);
        QueryTelemetry.ReadAll(workspace).Select(e => e.Tool).ToList().ShouldBe(["expand", "search", "repo_map"]);

        var lines = File.ReadAllLines(log);
        lines[0].ShouldContain(QueryTelemetry.RedactedPlaceholder);
        lines[0].ShouldNotContain(EnvPath);
        lines[^1].ShouldContain("\"tool\":\"repo_map\"");

        lockWasFreeMidScrub.ShouldBeFalse();
        linesMidScrub.ShouldBe(2);
        lateWriteLandedMidScrub.ShouldBeFalse();
    }

    [Fact]
    public async Task Redact_ParallelAppendsDuringTheScrub_LoseNoLineAndStayParseable()
    {
        var workspace = TelemetryStore();
        var log = SqueezrPaths.QueriesLogPath(workspace);
        Record(workspace, "expand", new { file = EnvPath, startLine = 1 });

        var inFlight = new CountdownEvent(Writers);
        var appends = Enumerable.Range(0, Writers).Select(thread => Task.Run(() =>
        {
            inFlight.Signal();
            for (var line = 0; line < LinesPerWriter; line++)
            {
                QueryTelemetry.Append(workspace, Entry($"w{thread}-{line}"));
            }
        })).ToArray();

        var released = false;
        var scrub = Task.Run(() => QueryTelemetry.RedactProtectedParams(workspace, path =>
        {
            if (!released)
            {
                released = true;
                inFlight.Wait(TimeSpan.FromSeconds(20)).ShouldBeTrue();
            }

            return BuiltInsOnly(path);
        }));

        (await Task.WhenAny(scrub, Task.Delay(SettleTimeoutMs))).ShouldBe(scrub);
        var drained = Task.WhenAll(appends);
        (await Task.WhenAny(drained, Task.Delay(SettleTimeoutMs))).ShouldBe(drained);
        (await scrub).ShouldBe(1);
        await drained;

        var expected = 1 + (Writers * LinesPerWriter);
        File.ReadAllLines(log).Length.ShouldBe(expected);
        var tools = QueryTelemetry.ReadAll(workspace).Select(e => e.Tool).ToList();
        tools.Count.ShouldBe(expected);
        tools[0].ShouldBe("expand");
        tools.Skip(1).OrderBy(tool => tool, StringComparer.Ordinal).ShouldBe(ExpectedWriterTools());
        File.ReadAllText(log).ShouldNotContain(EnvPath);
    }

    [Fact]
    public async Task ReadAll_AcrossTheScrubSwap_NeverSeesAPartialFile()
    {
        var workspace = TelemetryStore();
        var log = SqueezrPaths.QueriesLogPath(workspace);
        for (var line = 0; line < SeededLines; line++)
        {
            if (line == 7)
            {
                Record(workspace, "expand", new { file = EnvPath, startLine = line });
            }
            else
            {
                Record(workspace, "search", new { query = $"term-{line}" });
            }
        }

        var reads = 0;
        var held = false;
        var started = Stopwatch.GetTimestamp();
        var scrub = Task.Run(() => QueryTelemetry.RedactProtectedParams(workspace, path =>
        {
            if (!held)
            {
                held = true;
                var waited = Stopwatch.GetTimestamp();
                while (Volatile.Read(ref reads) < ReadsBeforeTheSwap
                    && Stopwatch.GetElapsedTime(waited) < TimeSpan.FromSeconds(20))
                {
                    Thread.Sleep(1);
                }
            }

            return BuiltInsOnly(path);
        }));

        while (!scrub.IsCompleted)
        {
            QueryTelemetry.ReadAll(workspace).Count.ShouldBe(SeededLines);
            Interlocked.Increment(ref reads);
        }

        (await scrub).ShouldBe(1);
        Volatile.Read(ref reads).ShouldBeGreaterThanOrEqualTo(ReadsBeforeTheSwap);
        Stopwatch.GetElapsedTime(started).TotalSeconds.ShouldBeLessThan(20);
        File.ReadAllLines(log).Length.ShouldBe(SeededLines);
        File.ReadAllText(log).ShouldNotContain(EnvPath);
        File.ReadAllText(log).ShouldContain(QueryTelemetry.RedactedPlaceholder);
    }

    [Fact]
    public void Redact_WhenAWriterHoldsOnPastTheBudget_SkipsThePassAndLeavesTheLogUntouched()
    {
        var workspace = TelemetryStore();
        var log = SqueezrPaths.QueriesLogPath(workspace);
        Record(workspace, "expand", new { file = EnvPath, startLine = 2 });
        var before = File.ReadAllBytes(log);

        using (var held = TelemetryWriteLock.TryAcquire(SqueezrPaths.StoreDir(workspace)))
        {
            held.ShouldNotBeNull();
            QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly).ShouldBe(0);
        }

        File.ReadAllBytes(log).ShouldBe(before);
        QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly).ShouldBe(1);
        File.ReadAllText(log).ShouldNotContain(EnvPath);
    }

    [Fact]
    public void Append_WhenAWriterHoldsOnPastTheBudget_StillWritesItsLine()
    {
        var workspace = TelemetryStore();
        Record(workspace, "search", new { query = "Load" });

        using (var held = TelemetryWriteLock.TryAcquire(SqueezrPaths.StoreDir(workspace)))
        {
            held.ShouldNotBeNull();
            QueryTelemetry.Append(workspace, Entry("stats"));
        }

        QueryTelemetry.ReadAll(workspace).Select(e => e.Tool).ToList().ShouldBe(["search", "stats"]);
    }

    // index.lock is held for the whole span of an index pass while queries.jsonl keeps taking a line from every
    // cli read and mcp call, so the two primitives must stay independent. Judged by what the append does under a
    // live index pass, not by the file layout: telemetry re-routed through IndexLock throws on the owner pid that
    // is still alive, Append swallows it, the row is gone. The queries lock is released before the append so the
    // append itself runs against an uncontended lock and lands immediately — no waiting, no budget.
    [Fact]
    public void TelemetryWriteLock_SitsBesideTheIndexLock_InsteadOfBehindIt()
    {
        var workspace = TelemetryStore();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var log = SqueezrPaths.QueriesLogPath(workspace);
        Record(workspace, "expand", new { file = EnvPath, startLine = 2 });

        using (IndexLock.Acquire(storeDir))
        {
            TelemetryWriteLock.LockPath(storeDir).ShouldNotBe(Path.Combine(storeDir, SqueezrPaths.LockFileName));
            using (var telemetry = TelemetryWriteLock.TryAcquire(storeDir))
            {
                telemetry.ShouldNotBeNull();
            }

            QueryTelemetry.Append(workspace, Entry("index-pass-running"));

            QueryTelemetry.ReadAll(workspace).Select(e => e.Tool).ToList().ShouldBe(["expand", "index-pass-running"]);
            QueryTelemetry.RedactProtectedParams(workspace, BuiltInsOnly).ShouldBe(1);
            File.ReadAllText(log).ShouldContain(QueryTelemetry.RedactedPlaceholder);
            File.ReadAllText(log).ShouldNotContain(EnvPath);
        }

        using var reacquired = TelemetryWriteLock.TryAcquire(storeDir);
        reacquired.ShouldNotBeNull();
    }

    private static bool BuiltInsOnly(string path) => SecretGuard.IsProtected(path, []);

    private static List<string> ExpectedWriterTools() =>
        Enumerable.Range(0, Writers)
            .SelectMany(thread => Enumerable.Range(0, LinesPerWriter).Select(line => $"w{thread}-{line}"))
            .OrderBy(tool => tool, StringComparer.Ordinal)
            .ToList();

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

    private static void Record(string workspaceRoot, string tool, object parameters) =>
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
