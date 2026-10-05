using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing.Query;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Mcp;

public class FindReferencesDedupTests
{
    [Fact]
    public async Task FindReferences_DefinitionsSharingOneRefSet_ReturnEachRowOnce()
    {
        var workspace = RefDedupFixture.NewInterfaceAndImplWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = RefDedupFixture.Symbol },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var refs = envelope.GetProperty("data").EnumerateArray().ToList();
        refs.Select(r => $"{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ShouldBe(RefDedupFixture.Callsites);

        var tokens = refs.Sum(r => TokenCounter.Estimate(
            $"{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()} {r.GetProperty("kind").GetString()}"));
        envelope.GetProperty("tokensUsed").GetInt32().ShouldBe(tokens);
    }

    [Fact]
    public async Task FindReferences_OverloadsWithOwnRefSets_KeepBothBlocksInOneFlatList()
    {
        var workspace = RefDedupFixture.NewOverloadWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var refs = ParseEnvelope(result).GetProperty("data").EnumerateArray().ToList();
        refs.Select(r => $"{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ShouldBe(["src/Runner.cs:7", "src/Other.cs:7"]);
    }

    [Fact]
    public async Task FindReferences_MirroredRepositories_ReturnBothCallsiteSets()
    {
        var workspace = RefDedupFixture.NewMirroredWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = RefDedupFixture.Symbol },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var refs = ParseEnvelope(result).GetProperty("data").EnumerateArray().ToList();
        refs.Count.ShouldBe(6);
        refs.Select(r => $"{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .GroupBy(x => x)
            .ShouldAllBe(g => g.Count() == 2);
    }

    [Fact]
    public async Task FindReferences_PartiallyOverlappingBlocks_ReturnTheUnionOnce()
    {
        var workspace = RefDedupFixture.NewInterfaceAndImplWorkspace();
        RefDedupFixture.PlantExtraRow(workspace, "src/Materializer.cs", "src/Guard.cs", 12);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = RefDedupFixture.Symbol },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        ParseEnvelope(result).GetProperty("data").EnumerateArray()
            .Select(r => $"{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ShouldBe(["src/Guard.cs:7", "src/Guard.cs:8", "src/Reporter.cs:7", "src/Guard.cs:12"]);
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
