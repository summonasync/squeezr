using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
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
        var envelope = ParseEnvelope(result);
        var refs = envelope.GetProperty("data").EnumerateArray().ToList();
        refs.Select(r => $"{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ShouldBe(["src/Runner.cs:7", "src/Other.cs:7"]);
        envelope.TryGetProperty("definitions", out _).ShouldBeFalse();
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
        var envelope = ParseEnvelope(result);
        var refs = envelope.GetProperty("data").EnumerateArray().ToList();
        refs.Count.ShouldBe(6);
        refs.Select(r => $"{r.GetProperty("repo").GetString()}:{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ShouldBe(
            [
                "alpha:src/Guard.cs:7", "alpha:src/Guard.cs:8", "alpha:src/Reporter.cs:7",
                "beta:src/Guard.cs:7", "beta:src/Guard.cs:8", "beta:src/Reporter.cs:7",
            ]);
        envelope.GetProperty("definitions").EnumerateArray()
            .Select(d => $"{d.GetProperty("repo").GetString()}:{d.GetProperty("file").GetString()}")
            .ShouldBe(
            [
                "alpha:src/IMaterializer.cs", "alpha:src/Materializer.cs",
                "beta:src/IMaterializer.cs", "beta:src/Materializer.cs",
            ]);
        string[] requiredKeys = ["cost", "data", "indexAge", "staleFiles", "tokensUsed", "truncated", "warnings"];
        envelope.EnumerateObject().Select(p => p.Name).ToList().ShouldBe([.. requiredKeys, "definitions"], ignoreOrder: true);
    }

    [Fact]
    public async Task FindReferences_MergedDefinitions_CarryKindAndDeclarationSpan()
    {
        var workspace = RefDedupFixture.NewInterfaceAndImplWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = RefDedupFixture.Symbol },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var definitions = ParseEnvelope(result).GetProperty("definitions").EnumerateArray().ToList();
        definitions.Select(d =>
            $"{d.GetProperty("repo").GetString()}:{d.GetProperty("kind").GetString()} " +
            $"{d.GetProperty("file").GetString()}:{d.GetProperty("startLine").GetInt32()}-" +
            $"{d.GetProperty("endLine").GetInt32()}")
            .ShouldBe(["demo:method src/IMaterializer.cs:5-5", "demo:method src/Materializer.cs:5-7"]);
    }

    [Fact]
    public async Task FindReferences_PartiallyOverlappingBlocks_ReturnTheUnionOnce()
    {
        var workspace = RefDedupFixture.NewInterfaceAndImplWorkspace();
        RefDedupFixture.PlantExtraRow(workspace, "demo", "src/Materializer.cs", "src/Guard.cs", 12);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = RefDedupFixture.Symbol },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var refs = ParseEnvelope(result).GetProperty("data").EnumerateArray().ToList();
        refs.Select(r => $"{r.GetProperty("repo").GetString()}:{r.GetProperty("file").GetString()}:{r.GetProperty("line").GetInt32()}")
            .ShouldBe(
            [
                "demo:src/Guard.cs:7", "demo:src/Guard.cs:8", "demo:src/Reporter.cs:7", "demo:src/Guard.cs:12",
            ]);
    }

    [Fact]
    public async Task FindReferences_UncalledDeclarationsInBothRepos_NameThemWithoutAnyRows()
    {
        var workspace = RefDedupFixture.NewMirroredWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Try" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("data").EnumerateArray().ShouldBeEmpty();
        envelope.GetProperty("definitions").EnumerateArray()
            .Select(d => $"{d.GetProperty("repo").GetString()}:{d.GetProperty("file").GetString()}:" +
                         $"{d.GetProperty("startLine").GetInt32()}-{d.GetProperty("endLine").GetInt32()}")
            .ShouldBe(["alpha:src/Guard.cs:5-9", "beta:src/Guard.cs:5-9"]);
    }

    [Fact]
    public async Task FindReferences_SingleUncalledDeclaration_StillNamesIt()
    {
        var workspace = RefDedupFixture.NewMirroredWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Try", ["repo"] = "alpha" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("data").EnumerateArray().ShouldBeEmpty();
        envelope.GetProperty("definitions").EnumerateArray()
            .Select(d => $"{d.GetProperty("repo").GetString()}:{d.GetProperty("file").GetString()}:" +
                         $"{d.GetProperty("startLine").GetInt32()}-{d.GetProperty("endLine").GetInt32()}")
            .ShouldBe(["alpha:src/Guard.cs:5-9"]);
    }

    [Fact]
    public async Task ToolDescription_DocumentsTheDefinitionsPresenceConditions()
    {
        var workspace = RefDedupFixture.NewInterfaceAndImplWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var references = tools.Single(t => t.Name == "find_references");

        references.Description.ShouldContain("Every row names its repo");
        references.Description.ShouldContain("definitions ({repo, kind, file, startLine, endLine})");
        references.Description.ShouldContain("two or more same-name declarations share one callsite list");
        references.Description.ShouldContain("nothing calls the symbol at all");
    }

    [Fact]
    public async Task FindReferences_MergedDefinitionsWithProtectedTwin_NeverNamesTheSecret()
    {
        var workspace = SecretGuardFixture.NewIndexedWorkspace();
        // three same-name declarations with no callsites merge into one definitions block; the protected one
        // would join it if the guard ran after the merge, so this is the shape where the new field can leak
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.EnvPath, "Vault", "method");
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.ExamplePath, "Vault", "method");
        SecretGuardFixture.PlantSymbol(workspace, SecretGuardFixture.AppPath, "Vault", "field");
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "find_references",
            new Dictionary<string, object?> { ["symbol"] = "Vault" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var definitions = envelope.GetProperty("definitions");
        definitions.EnumerateArray()
            .Select(d => d.GetProperty("file").GetString()!)
            .ShouldBe([SecretGuardFixture.ExamplePath, SecretGuardFixture.AppPath]);
        // the quoted needle is the whole path: a bare '.env' substring would also catch the public '.env.example'
        definitions.GetRawText().ShouldNotContain($"\"{SecretGuardFixture.EnvPath}\"");
        envelope.GetProperty("data").EnumerateArray().ShouldBeEmpty();
        Warnings(envelope).ShouldContain(w => w.StartsWith("secret guard:", StringComparison.Ordinal));
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static IReadOnlyList<string> Warnings(JsonElement envelope) =>
        [.. envelope.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!)];
}
