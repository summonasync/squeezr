using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;

namespace Squeezr.Tests.Cli;

public class IndexEventWriterTests
{
    [Fact]
    public void Started_WithProvider_SerializesProviderField()
    {
        var writer = new StringWriter();

        new IndexEventWriter(writer).Started("/ws", provider: "onnx");

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.GetProperty("type").GetString().ShouldBe("started");
        doc.RootElement.GetProperty("provider").GetString().ShouldBe("onnx");
    }

    [Fact]
    public void Started_WithoutProvider_OmitsProviderField()
    {
        var writer = new StringWriter();

        new IndexEventWriter(writer).Started("/ws", changedPaths: 3);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.GetProperty("changedPaths").GetInt32().ShouldBe(3);
        doc.RootElement.TryGetProperty("provider", out _).ShouldBeFalse();
    }

    [Fact]
    public void Completed_WithProvider_SerializesProviderField()
    {
        var writer = new StringWriter();
        var result = new IndexResult([], DateTime.UtcNow, []);

        new IndexEventWriter(writer).Completed(result, 42, null, provider: "ollama");

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.GetProperty("type").GetString().ShouldBe("completed");
        doc.RootElement.GetProperty("provider").GetString().ShouldBe("ollama");
    }

    [Fact]
    public void Completed_WithoutProvider_OmitsProviderField()
    {
        var writer = new StringWriter();
        var result = new IndexResult([new RepoIndexResult("demo", 1, 0, 0, 0, 1, 0)], DateTime.UtcNow, []);

        new IndexEventWriter(writer).Completed(result, 7, 2);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.GetProperty("symbols").GetInt32().ShouldBe(2);
        doc.RootElement.TryGetProperty("provider", out _).ShouldBeFalse();
    }

    [Fact]
    public void Completed_CarriesTheReapQueueCounters()
    {
        var writer = new StringWriter();
        var result = new IndexResult([], DateTime.UtcNow, [], DeferredReaps: 2, PendingReaps: 5);

        new IndexEventWriter(writer).Completed(result, 7, null);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.GetProperty("deferredReaps").GetInt32().ShouldBe(2);
        doc.RootElement.GetProperty("pendingReaps").GetInt32().ShouldBe(5);
    }

    [Fact]
    public void Completed_ReapCountersAreAlwaysPresent_ZeroIncluded()
    {
        var writer = new StringWriter();

        new IndexEventWriter(writer).Completed(new IndexResult([], DateTime.UtcNow, []), 7, null);

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.GetProperty("deferredReaps").GetInt32().ShouldBe(0);
        doc.RootElement.GetProperty("pendingReaps").GetInt32().ShouldBe(0);
    }
}
