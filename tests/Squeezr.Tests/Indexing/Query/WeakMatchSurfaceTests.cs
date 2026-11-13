using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.RankEval;
using Squeezr.Cli.Tui;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Mcp;
using Squeezr.Tests.Tui;

namespace Squeezr.Tests.Indexing.Query;

// The weak-match sentences are produced once, in SearchEngine.Finalize, out of the key the provider already
// carries. Each surface owes the answer that channel verbatim, so all four are pinned with an active key that
// has no floor under it: the channel stays alive, and neither the low-confidence claim nor the calibration
// hint appears — an answer whose semantic arm produced no scores has nothing to judge.
[Collection("ProcessGlobals")]
public sealed class WeakMatchSurfaceTests : IDisposable
{
    private const string ApiKeyEnv = "SQUEEZR_TESTS_WEAK_MATCH_SURFACE_KEY";

    // the key a provider built from the shipped OpenAI-compatible defaults carries: no measured floor
    private static readonly string UncalibratedKey = EmbeddingModelKey.From(new EmbeddingsConfig
    {
        Provider = "openai"
    });

    public WeakMatchSurfaceTests()
        => Environment.SetEnvironmentVariable(ApiKeyEnv, "test-key");

    public void Dispose()
        => Environment.SetEnvironmentVariable(ApiKeyEnv, null);

    [Fact]
    public void ModelKey_IsARequiredInterfaceMember_NoProviderCanReachAnEngineWithoutOne()
    {
        var getter = typeof(IEmbeddingProvider).GetProperty("ModelKey")!.GetGetMethod()!;

        getter.IsAbstract.ShouldBeTrue();
        // the same probe answers false for a defaulted member, so the pin above measured something
        typeof(IStatesKeyWithDefault).GetProperty("ModelKey")!.GetGetMethod()!.IsAbstract.ShouldBeFalse();
    }

    [Fact]
    public async Task McpCodeSearch_UncalibratedKey_RendersTheWarningChannelAndClaimsNothing()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "zzznotaword" },
            cancellationToken: CancellationToken.None);

        var warnings = ParseEnvelope(result).GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()!)
            .ToList();

        AssertStatedKeyIsUncalibrated(workspace);
        warnings.ShouldContain(SearchEngine.VectorStoreUnavailableWarning);
        warnings.ShouldNotContain(w => w.Contains("at low confidence", StringComparison.Ordinal));
        warnings.ShouldNotContain(w => w.Contains("weak-match floor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CliSearch_UncalibratedKey_RendersTheWarningChannelAndClaimsNothing()
    {
        var workspace = NewIndexedWorkspace();

        var search = await CliTestProcess.RunAsync(workspace, "search", "zzznotaword", "--json");

        search.ExitCode.ShouldBe(0);
        AssertStatedKeyIsUncalibrated(workspace);
        using var doc = JsonDocument.Parse(search.StdErr.Trim());
        doc.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetProperty("code").GetString())
            .ShouldContain("vector_store_unavailable");
        search.StdErr.ShouldNotContain("at low confidence");
        search.StdErr.ShouldNotContain("weak-match floor");
    }

    [Fact]
    public async Task TuiSearch_UncalibratedKey_RendersTheWarningChannelAndClaimsNothing()
    {
        var workspace = NewIndexedWorkspace();
        var console = TuiTestHarness.InteractiveConsole();
        console.Input.PushTextWithEnter("zzznotaword");
        console.Input.PushKey(ConsoleKey.Enter);

        await TuiSearchFlow.RunAsync(console, workspace, new TuiSession(), CancellationToken.None);

        AssertStatedKeyIsUncalibrated(workspace);
        console.Output.ShouldContain(SearchEngine.VectorStoreUnavailableWarning);
        console.Output.ShouldNotContain("at low confidence");
        console.Output.ShouldNotContain("weak-match floor");
    }

    [Fact]
    public async Task RankEval_UncalibratedKey_RendersTheWarningChannelAndClaimsNothing()
    {
        var workspace = NewIndexedWorkspace();

        var outcome = await new RankEvalRunner(workspace).RunAsync(
            [new GoldenQuery("zzznotaword", null, null, ["repo/Calc.cs"], "surface pin")],
            CancellationToken.None);

        AssertStatedKeyIsUncalibrated(workspace);
        outcome.Warnings.ShouldContain(SearchEngine.VectorStoreUnavailableWarning);
        outcome.Warnings.ShouldNotContain(w => w.Contains("at low confidence", StringComparison.Ordinal));
        outcome.Warnings.ShouldNotContain(w => w.Contains("weak-match floor", StringComparison.Ordinal));
    }

    // the surfaces resolve their runtime from the workspace config, so the key this asserts is the one
    // the surface under test really handed the engine on the run above
    private static void AssertStatedKeyIsUncalibrated(string workspace)
    {
        var warnings = EmbeddingRuntimeFactory.TryCreate(workspace, withProvider: true, out var provider, out _);

        warnings.ShouldBeEmpty();
        provider!.ModelKey.ShouldBe(UncalibratedKey);
        UncalibratedKey.ShouldBe("openai/text-embedding-3-large/1024");
        WeakMatchPolicy.HasCalibratedFloor(UncalibratedKey).ShouldBeFalse();
    }

    private static JsonElement ParseEnvelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        manager.AddRepo("repo", "demo");
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
        {
            Embeddings = new EmbeddingsConfig
            {
                Provider = "openai",
                OpenAiCompatible = new OpenAiCompatibleOptions { ApiKeyEnv = ApiKeyEnv }
            },
            Qdrant = new QdrantConfig { Endpoint = TestWorkspaceConfig.DeadQdrantEndpoint }
        });
        File.WriteAllText(
            Path.Combine(workspace, "repo", "Calc.cs"),
            """
            class Calc
            {
                int Add(int a, int b) { return a + b; }
            }
            """);
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }
}

file interface IStatesKeyWithDefault
{
    string ModelKey => "ollama/defaulted/4";
}
