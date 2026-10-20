using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Indexing;

namespace Squeezr.Tests.Mcp;

public sealed class ServeWatchStatusTests
{
    [Fact]
    public void PassCompleted_ReapDebt_EmitsOneMachineReadableLineWithBothCounters()
    {
        var writer = new StringWriter();
        var status = new ServeWatchStatus(writer);
        var result = new IndexResult(
            [new RepoIndexResult("demo", 0, 0, 2, 0, 8, 0)], DateTime.UtcNow,
            ["this pass carries no vector store; point cleanup for 2 file(s) deferred to a later pass"],
            DeferredReaps: 2, PendingReaps: 3);

        status.PassCompleted(result);

        var lines = Lines(writer);
        lines.Length.ShouldBe(1);
        using var doc = JsonDocument.Parse(lines[0]);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("watchPass");
        doc.RootElement.GetProperty("deferredReaps").GetInt32().ShouldBe(2);
        doc.RootElement.GetProperty("pendingReaps").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void PassCompleted_StandingQueue_EvenWithoutNewDeletionsIsStillReported()
    {
        var writer = new StringWriter();
        var status = new ServeWatchStatus(writer);

        status.PassCompleted(new IndexResult([], DateTime.UtcNow, [], DeferredReaps: 0, PendingReaps: 1));

        Lines(writer).ShouldHaveSingleItem().ShouldContain("\"pendingReaps\":1");
    }

    [Fact]
    public void PassCompleted_CleanPass_EmitsNothing()
    {
        var writer = new StringWriter();
        var status = new ServeWatchStatus(writer);

        status.PassStarted(3);
        status.PassCompleted(new IndexResult([new RepoIndexResult("demo", 1, 0, 0, 0, 1, 0)], DateTime.UtcNow, []));
        status.RetryScheduled(TimeSpan.FromSeconds(2));

        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void PassCompleted_EmbeddedChunks_EmitADrainLineWithTheCount()
    {
        var writer = new StringWriter();
        var status = new ServeWatchStatus(writer);

        status.PassCompleted(new IndexResult(
            [new RepoIndexResult("demo", 0, 1, 0, 0, 2, 0, null, 0, 0, 0, 12)], DateTime.UtcNow, [],
            Embedded: 12));

        var line = Lines(writer).ShouldHaveSingleItem();
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("watchDrain");
        doc.RootElement.GetProperty("embedded").GetInt32().ShouldBe(12);
        doc.RootElement.TryGetProperty("idleReason", out _).ShouldBeFalse();
    }

    [Fact]
    public void PassCompleted_RepeatedIdleReason_StaysSilent_UntilItChangesOrTheDrainResumes()
    {
        var writer = new StringWriter();
        var status = new ServeWatchStatus(writer);
        var idle = new IndexResult(
            [new RepoIndexResult("demo", 0, 0, 0, 0, 2, 0)], DateTime.UtcNow,
            ["embeddings skipped for repo 'demo': vector store is unavailable"]);

        status.PassCompleted(idle);
        status.PassCompleted(idle);
        status.PassCompleted(new IndexResult(
            [new RepoIndexResult("demo", 0, 0, 0, 0, 2, 0, null, 0, 0, 0, 5)], DateTime.UtcNow, [],
            Embedded: 5));
        status.PassCompleted(idle);

        var lines = Lines(writer);
        lines.Length.ShouldBe(3);
        using var first = JsonDocument.Parse(lines[0]);
        first.RootElement.GetProperty("embedded").GetInt32().ShouldBe(0);
        var idleReason = first.RootElement.GetProperty("idleReason").GetString();
        idleReason.ShouldNotBeNull();
        idleReason.ShouldContain("vector store is unavailable");
        using var second = JsonDocument.Parse(lines[1]);
        second.RootElement.GetProperty("embedded").GetInt32().ShouldBe(5);
        using var third = JsonDocument.Parse(lines[2]);
        third.RootElement.GetProperty("embedded").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void Faulted_RepeatsOfTheSameFaultStaySilent_ChangedFaultsAreHeard()
    {
        var writer = new StringWriter();
        var status = new ServeWatchStatus(writer);

        status.Faulted("index pass failed: boom");
        status.Faulted("index pass failed: boom");
        status.Faulted("index pass failed: worse");

        var lines = Lines(writer);
        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("warning: serve watch: index pass failed: boom");
        lines[1].ShouldBe("warning: serve watch: index pass failed: worse");
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
