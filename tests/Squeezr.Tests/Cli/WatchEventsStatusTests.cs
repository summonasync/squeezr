using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;

namespace Squeezr.Tests.Cli;

public sealed class WatchEventsStatusTests
{
    [Fact]
    public void PassStarted_EmitsStartedWithChangedPaths_AndResetsRenderer()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));
        renderer.PhaseStarted("demo", IndexPhase.Files, 1);
        renderer.ItemProcessed("demo", "a.cs");
        var status = new WatchEventsStatus(new IndexEventWriter(writer), "/ws", renderer);

        status.PassStarted(4);

        var lines = Lines(writer);
        lines.Length.ShouldBe(2);
        using (var doc = JsonDocument.Parse(lines[1]))
        {
            var e = doc.RootElement;
            e.GetProperty("type").GetString().ShouldBe("started");
            e.GetProperty("workspace").GetString().ShouldBe("/ws");
            e.GetProperty("changedPaths").GetInt32().ShouldBe(4);
        }

        renderer.PhaseStarted("demo", IndexPhase.Files, 1);
        renderer.ItemProcessed("demo", "a.cs");
        renderer.Symbols.ShouldBe(0);
        Lines(writer)[2].ShouldContain("\"files\":1");
    }

    [Fact]
    public void PassCompleted_EmitsCompletedWithTotalsDurationAndSymbols()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));
        var status = new WatchEventsStatus(new IndexEventWriter(writer), "/ws", renderer);
        var result = new IndexResult(
            [new RepoIndexResult("demo", 2, 1, 0, 0, 5, 0)], DateTime.UtcNow, []);

        status.PassStarted(1);
        renderer.PhaseStarted("demo", IndexPhase.Semantic, 1);
        renderer.ItemProcessed("demo", "a.cs", 7);
        status.PassCompleted(result);

        var lines = Lines(writer);
        lines.Length.ShouldBe(3);
        ParseType(lines[0]).ShouldBe("started");
        ParseType(lines[1]).ShouldBe("progress");
        using var doc = JsonDocument.Parse(lines[2]);
        var e = doc.RootElement;
        e.GetProperty("type").GetString().ShouldBe("completed");
        e.GetProperty("durationMs").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        e.GetProperty("files").GetInt32().ShouldBe(5);
        e.GetProperty("added").GetInt32().ShouldBe(2);
        e.GetProperty("modified").GetInt32().ShouldBe(1);
        e.GetProperty("symbols").GetInt32().ShouldBe(7);
        e.GetProperty("repos")[0].GetProperty("name").GetString().ShouldBe("demo");
    }

    [Fact]
    public void Faulted_EmitsErrorEvent_RetryScheduled_EmitsNothing()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));
        var status = new WatchEventsStatus(new IndexEventWriter(writer), "/ws", renderer);

        status.RetryScheduled(TimeSpan.FromSeconds(1));
        writer.ToString().ShouldBeEmpty();

        status.Faulted("index pass failed: boom");

        var lines = Lines(writer);
        lines.Length.ShouldBe(1);
        using var doc = JsonDocument.Parse(lines[0]);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("error");
        doc.RootElement.GetProperty("code").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldBe("index pass failed: boom");
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ParseType(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("type").GetString()!;
    }
}
