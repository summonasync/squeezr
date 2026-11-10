using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class CodePackToolTests
{
    private const int MemberCount = 8;
    private const int MappedPackBudget = 120;
    private const int MemberRowsBudget = 25;
    private const int BodyOnlyBudget = 20;

    [Fact]
    public async Task CodePack_KnownSymbol_CompletePackWithoutRemedyNoise()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();

        var data = envelope.GetProperty("data");
        data.GetProperty("name").GetString().ShouldBe("Add");
        data.GetProperty("signature").GetString().ShouldNotBeNull();
        data.GetProperty("bodyLines").EnumerateArray().ShouldNotBeEmpty();
        data.GetProperty("callers").EnumerateArray().ShouldNotBeEmpty();
        data.GetProperty("withheldCallers").GetInt32().ShouldBe(0);
        data.GetProperty("complete").GetBoolean().ShouldBeTrue();

        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue();

        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .Count(w => w.Contains("withheld") || w.StartsWith("truncated: "))
            .ShouldBe(0);
    }

    [Fact]
    public async Task CodePack_WithheldCallers_CompleteFalseWithItsOwnLineAndNoTruncationLine()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);
        var full = FullLookupTokens(workspace, "Add");

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Add", ["maxTokens"] = full },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();

        var data = envelope.GetProperty("data");
        data.GetProperty("complete").GetBoolean().ShouldBeFalse();
        data.GetProperty("withheldCallers").GetInt32().ShouldBeGreaterThan(0);
        data.GetProperty("bodyLines").EnumerateArray().ShouldNotBeEmpty();
        data.GetProperty("lookupTruncated").GetBoolean().ShouldBeFalse();

        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeFalse();
        var warnings = Warnings(envelope);
        warnings.ShouldContain(w => w.Contains("caller(s) withheld to fit the budget"));
        warnings.Count(w => w.StartsWith("truncated: ")).ShouldBe(0);
    }

    [Fact]
    public async Task CodePack_MappedLookup_AnswersCompleteWithWholeMemberMapAndNoWarning()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Widget", ["maxTokens"] = MappedPackBudget },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue();

        var data = envelope.GetProperty("data");
        data.GetProperty("complete").GetBoolean().ShouldBeTrue();
        data.GetProperty("lookupTruncated").GetBoolean().ShouldBeFalse();
        data.GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();
        data.GetProperty("bodyLines").EnumerateArray().ShouldBeEmpty();
        data.GetProperty("members").EnumerateArray().Count().ShouldBe(MemberCount);
        data.GetProperty("membersOmitted").GetInt32().ShouldBe(0);
        data.GetProperty("callers").EnumerateArray().ShouldNotBeEmpty();
        data.GetProperty("withheldCallers").GetInt32().ShouldBe(0);

        var warnings = Warnings(envelope);
        warnings.Count(w => w.StartsWith("truncated: ")).ShouldBe(0);
        warnings.ShouldNotContain(w => w.Contains("withheld"));
    }

    [Fact]
    public async Task CodePack_MappedLookup_CachedRepeatAnswerKeepsTheHonestLabels()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);
        var arguments = new Dictionary<string, object?>
        {
            ["name"] = "Widget",
            ["maxTokens"] = MappedPackBudget,
        };

        var fresh = await harness.Client.CallToolAsync(
            "code_pack", arguments, cancellationToken: CancellationToken.None);
        fresh.IsError.ShouldNotBe(true);
        var cached = ParseEnvelope(await harness.Client.CallToolAsync(
            "code_pack", arguments, cancellationToken: CancellationToken.None));

        cached.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        cached.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue();
        cached.GetProperty("data").GetProperty("complete").GetBoolean().ShouldBeTrue();
        cached.GetProperty("data").GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();
        cached.GetProperty("data").GetProperty("members").EnumerateArray().Count().ShouldBe(MemberCount);
        Warnings(cached).ShouldNotContain(w => w.StartsWith("truncated: "));

        // the label checks above are only worth something if this answer really came from the cache
        File.ReadAllLines(SqueezrPaths.QueriesLogPath(workspace))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Last(e => e.GetProperty("tool").GetString() == "code_pack")
            .GetProperty("cacheHit").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task CodePack_MemberRowsRefused_AnswersIncompleteWithTheMemberRemedy()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Widget", ["maxTokens"] = MemberRowsBudget },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var data = envelope.GetProperty("data");
        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        envelope.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeFalse();
        data.GetProperty("complete").GetBoolean().ShouldBeFalse();
        data.GetProperty("lookupTruncated").GetBoolean().ShouldBeTrue();
        data.GetProperty("membersOmitted").GetInt32().ShouldBeGreaterThan(0);

        Warnings(envelope).ShouldContain(w => w ==
            $"truncated: {data.GetProperty("lookupTokens").GetInt32()} of {MemberRowsBudget} token ceiling " +
            "— read a member with expand_span or raise maxTokens");
    }

    [Fact]
    public async Task CodePack_BodyRefusedWithoutSubstitute_RemedyNamesTheBodyPrice()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Run", ["maxTokens"] = BodyOnlyBudget },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        var data = envelope.GetProperty("data");
        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        data.GetProperty("complete").GetBoolean().ShouldBeFalse();
        data.GetProperty("bodyOmitted").GetBoolean().ShouldBeTrue();
        data.GetProperty("members").EnumerateArray().ShouldBeEmpty();

        Warnings(envelope).Single(w => w.StartsWith("truncated: ")).ShouldMatch(
            @"^truncated: \d+ of 20 token ceiling — raise maxTokens \(the body alone needs ~\d+ tokens\)$");
    }

    [Fact]
    public async Task CodePack_HeaderRefused_RemedyNamesQueryNarrowing()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Widget", ["maxTokens"] = 1 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        Warnings(envelope).ShouldContain(
            w => w == "truncated: 0 of 1 token ceiling — narrow the query (repo, kind) or raise maxTokens");
    }

    [Fact]
    public async Task CodePack_TelemetryRow_CarriesToolNameAndBaseline()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Add" },
            cancellationToken: CancellationToken.None);

        var line = File.ReadAllLines(SqueezrPaths.QueriesLogPath(workspace))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Last(e => e.GetProperty("tool").GetString() == "code_pack");
        line.GetProperty("tokensUsed").GetInt32().ShouldBeGreaterThan(0);
        line.GetProperty("baselineTokens").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task CodePack_NotFound_NamesItInsteadOfPosing()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "Nope" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ShouldContain(w => w.Contains("No symbol named 'Nope' found"));
        envelope.GetProperty("cost").TryGetProperty("baselineTokens", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task CodePack_SuggestionTextRefused_OmitsDidYouMeanAndStaysClipped()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "MaterializationServce", ["maxTokens"] = 1 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        envelope.TryGetProperty("didYouMean", out _).ShouldBeFalse();
        envelope.GetProperty("data").GetProperty("lookupTruncated").GetBoolean().ShouldBeTrue();
        Warnings(envelope).ShouldContain(
            w => w == "truncated: 0 of 1 token ceiling — raise maxTokens");
    }

    [Fact]
    public async Task CodePack_SuggestionTextFits_KeepsDidYouMeanAndAnswersComplete()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_pack",
            new Dictionary<string, object?> { ["name"] = "MaterializationServce", ["maxTokens"] = 2000 },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = ParseEnvelope(result);
        envelope.GetProperty("didYouMean").EnumerateArray().Select(s => s.GetString()!)
            .ShouldContain("Materializer");
        envelope.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        envelope.GetProperty("data").GetProperty("lookupTruncated").GetBoolean().ShouldBeFalse();
        Warnings(envelope).ShouldNotContain(w => w.StartsWith("truncated: "));
    }

    private static int FullLookupTokens(string workspace, string name) =>
        new Squeezr.Core.Indexing.Navigation.SymbolPackComposer(workspace)
            .Compose(name, null, null, 2000).LookupTokens;

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static IReadOnlyList<string> Warnings(JsonElement envelope) =>
        envelope.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        WriteFile(workspace, "repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public string Add(string a, string b)
                {
                    return a + b;
                }
            }
            """);
        WriteFile(workspace, "repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return new Calc().Add(1, 2);
                }
            }
            """);
        WriteFile(workspace, "repo/src/Materializer.cs", """
            namespace Demo;

            public class Materializer
            {
                public string Flatten(string input)
                {
                    return input.Trim();
                }
            }
            """);
        WriteFile(workspace, "repo/src/Widget.cs", WidgetClass());
        WriteFile(workspace, "repo/src/Client.cs", """
            namespace Demo;

            public class Client
            {
                public int Run()
                {
                    return new Widget().Part01(1);
                }
            }
            """);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static string WidgetClass()
    {
        var text = new StringBuilder("namespace Demo;\n\npublic class Widget\n{\n");
        foreach (var index in Enumerable.Range(1, MemberCount))
        {
            text.Append($"    public int Part{index:00}(int a)\n");
            text.Append("    {\n");
            text.Append($"        return a * {index};\n");
            text.Append("    }\n\n");
        }

        return text.Append("}\n").ToString();
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
