using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;

namespace Squeezr.Tests.Cli;

public sealed class IndexEventsRendererTests
{
    [Fact]
    public void PhaseStarted_EmitsProgressOnlyForWorkPhasesWithKnownTotals()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));

        renderer.PhaseStarted("demo", IndexPhase.Walk, 0);
        renderer.PhaseStarted("demo", IndexPhase.Files, 3);
        writer.ToString().ShouldBeEmpty();

        renderer.PhaseStarted("demo", IndexPhase.Semantic, 2);
        renderer.PhaseStarted("demo", IndexPhase.Vectors, 0);
        renderer.PhaseStarted("demo", IndexPhase.Trigram, 1);

        var lines = Lines(writer);
        lines.Length.ShouldBe(2);
        AssertLine(lines[0], "progress", e =>
        {
            e.GetProperty("repo").GetString().ShouldBe("demo");
            e.GetProperty("phase").GetString().ShouldBe("semantic");
            e.GetProperty("total").GetInt32().ShouldBe(2);
        });
        AssertLine(lines[1], "progress", e => e.GetProperty("phase").GetString().ShouldBe("trigram"));
    }

    [Fact]
    public void ItemProcessed_EmitsFileIndexedOnlyDuringFilesPhase_WithCumulativeCounts()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));

        renderer.PhaseStarted("demo", IndexPhase.Files, 2);
        renderer.ItemProcessed("demo", "a.cs");
        renderer.ItemProcessed("demo", "b.cs");

        var lines = Lines(writer);
        lines.Length.ShouldBe(2);
        AssertLine(lines[0], "fileIndexed", e =>
        {
            e.GetProperty("path").GetString().ShouldBe("a.cs");
            e.GetProperty("files").GetInt32().ShouldBe(1);
        });
        AssertLine(lines[1], "fileIndexed", e => e.GetProperty("files").GetInt32().ShouldBe(2));
    }

    [Fact]
    public void ItemProcessed_DuringSemanticAndEmbeddings_EmitsNoFileEvents()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));

        renderer.PhaseStarted("demo", IndexPhase.Semantic, 1);
        renderer.ItemProcessed("demo", "a.cs", 4);
        renderer.PhaseStarted("demo", IndexPhase.Vectors, 1);
        renderer.ItemProcessed("demo", "chunkhash");
        renderer.PhaseStarted("demo", IndexPhase.Trigram, 1);
        renderer.ItemProcessed("demo", "a.cs");

        var lines = Lines(writer);
        lines.ShouldAllBe(line => ParseType(line) == "progress");
        renderer.Symbols.ShouldBe(4);
    }

    [Fact]
    public void ItemProcessed_DuringEmbeddings_EmitsPeriodicProgressWithDoneAndNoPerChunkLines()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));

        renderer.PhaseStarted("demo", IndexPhase.Vectors, 1024);
        for (var i = 0; i < 1025; i++)
        {
            renderer.ItemProcessed("demo", $"chunk{i}");
        }

        var lines = Lines(writer);
        lines.Length.ShouldBe(3);
        lines[0].ShouldBe("""{"type":"progress","repo":"demo","phase":"embeddings","total":1024}""");
        lines[1].ShouldBe("""{"type":"progress","repo":"demo","phase":"embeddings","total":1024,"done":512}""");
        lines[2].ShouldBe("""{"type":"progress","repo":"demo","phase":"embeddings","total":1024,"done":1024}""");
    }

    [Fact]
    public void ItemProcessed_DuringFiles_EmitsLegacyFileIndexedLinesByteForByte()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));

        renderer.PhaseStarted("demo", IndexPhase.Files, 2);
        renderer.ItemProcessed("demo", "a.cs");
        renderer.ItemProcessed("demo", "b.cs");

        var lines = Lines(writer);
        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("""{"type":"fileIndexed","repo":"demo","path":"a.cs","files":1}""");
        lines[1].ShouldBe("""{"type":"fileIndexed","repo":"demo","path":"b.cs","files":2}""");
    }

    [Fact]
    public void ItemProcessed_DuringEmbeddings_ResetsDoneCounterPerRepo()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));

        renderer.PhaseStarted("alpha", IndexPhase.Vectors, 600);
        for (var i = 0; i < 600; i++)
        {
            renderer.ItemProcessed("alpha", $"a{i}");
        }

        renderer.PhaseStarted("beta", IndexPhase.Vectors, 600);
        for (var i = 0; i < 512; i++)
        {
            renderer.ItemProcessed("beta", $"b{i}");
        }

        var lines = Lines(writer);
        lines.Length.ShouldBe(4);
        AssertLine(lines[0], "progress", e =>
        {
            e.GetProperty("repo").GetString().ShouldBe("alpha");
            e.TryGetProperty("done", out _).ShouldBeFalse();
        });
        AssertLine(lines[1], "progress", e =>
        {
            e.GetProperty("repo").GetString().ShouldBe("alpha");
            e.GetProperty("done").GetInt32().ShouldBe(512);
        });
        AssertLine(lines[2], "progress", e =>
        {
            e.GetProperty("repo").GetString().ShouldBe("beta");
            e.TryGetProperty("done", out _).ShouldBeFalse();
        });
        AssertLine(lines[3], "progress", e =>
        {
            e.GetProperty("repo").GetString().ShouldBe("beta");
            e.GetProperty("done").GetInt32().ShouldBe(512);
        });
    }

    [Fact]
    public void Symbols_AccumulateAcrossPerPhaseCumulativeCountersWithoutDoubleCounting()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));

        renderer.PhaseStarted("demo", IndexPhase.Semantic, 2);
        renderer.ItemProcessed("demo", "a.cs", 3);
        renderer.ItemProcessed("demo", "b.cs", 5);
        renderer.PhaseStarted("demo", IndexPhase.Semantic, 1);
        renderer.ItemProcessed("demo", "c.ts", 2);

        renderer.Symbols.ShouldBe(7);
    }

    [Fact]
    public void Reset_ClearsCumulativeCountersForNextPass()
    {
        var writer = new StringWriter();
        var renderer = new IndexEventsRenderer(new IndexEventWriter(writer));

        renderer.PhaseStarted("demo", IndexPhase.Files, 1);
        renderer.ItemProcessed("demo", "a.cs");
        renderer.PhaseStarted("demo", IndexPhase.Semantic, 1);
        renderer.ItemProcessed("demo", "a.cs", 4);
        renderer.Reset();

        renderer.PhaseStarted("demo", IndexPhase.Files, 1);
        renderer.ItemProcessed("demo", "a.cs");

        var lines = Lines(writer);
        lines.Length.ShouldBe(3);
        AssertLine(lines[2], "fileIndexed", e => e.GetProperty("files").GetInt32().ShouldBe(1));
        renderer.Symbols.ShouldBe(0);
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ParseType(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("type").GetString()!;
    }

    private static void AssertLine(string line, string type, Action<JsonElement> assert)
    {
        using var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("type").GetString().ShouldBe(type);
        assert(doc.RootElement);
    }
}
