using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpIndexNotBuiltRemedyTests
{
    private const string Sentence = "Index is not built. Run 'squeezr index' first.";

    [Theory]
    [InlineData("ollama", true)]
    [InlineData("auto", true)]
    [InlineData("openai", false)]
    public async Task Wire_UnbuiltIndex_RemedyFollowsTheProviderOfThatWorkspace(string provider, bool needsServices)
    {
        var root = UnbuiltWorkspace(provider);
        await using var harness = await McpTestHarness.StartAsync(root);

        var payload = await CallUnbuiltIndexAsync(harness, "symbol_lookup", ("name", "Add"));

        var steps = Steps(payload);
        var anchored = $"cd '{root}' && squeezr index";
        if (needsServices)
        {
            steps.ShouldBe(["squeezr services up", anchored]);
            payload.GetProperty("cause").GetString().ShouldBe("provider_needs_services");
        }
        else
        {
            steps.ShouldBe([anchored]);
            HasCause(payload).ShouldBeFalse();
        }

        payload.GetProperty("code").GetString().ShouldBe("index_not_built");
    }

    [Fact]
    public async Task Wire_UnbuiltIndexWithoutConfigFile_ServesTheIndexStepAndGuessesNothing()
    {
        var root = UnbuiltWorkspace("ollama");
        File.Delete(SqueezrPaths.ConfigPath(root));
        await using var harness = await McpTestHarness.StartAsync(root);

        var payload = await CallUnbuiltIndexAsync(harness, "symbol_lookup", ("name", "Add"));

        Steps(payload).ShouldBe([$"cd '{root}' && squeezr index"]);
        HasCause(payload).ShouldBeFalse();
        payload.GetProperty("code").GetString().ShouldBe("index_not_built");
    }

    [Fact]
    public async Task Wire_UnbuiltIndexWithCorruptConfig_ServesTheIndexStepAndGuessesNothing()
    {
        var root = UnbuiltWorkspace("ollama");
        File.WriteAllText(SqueezrPaths.ConfigPath(root), "{ not json");
        await using var harness = await McpTestHarness.StartAsync(root);

        var payload = await CallUnbuiltIndexAsync(harness, "symbol_lookup", ("name", "Add"));

        Steps(payload).ShouldBe([$"cd '{root}' && squeezr index"]);
        HasCause(payload).ShouldBeFalse();
    }

    [Fact]
    public async Task Wire_UnbuiltIndex_NavToolAndSearchShareOneCodeAndRemedy()
    {
        var root = UnbuiltWorkspace("ollama");
        await using var harness = await McpTestHarness.StartAsync(root);

        var fromSearch = await CallUnbuiltIndexAsync(harness, "code_search", ("query", "Add"));
        var fromLookup = await CallUnbuiltIndexAsync(harness, "symbol_lookup", ("name", "Add"));

        fromSearch.GetProperty("code").GetString().ShouldBe(WorkspaceException.IndexNotBuiltCode);
        fromLookup.GetProperty("code").GetString().ShouldBe(WorkspaceException.IndexNotBuiltCode);
        Steps(fromLookup).ShouldBe(Steps(fromSearch));
    }

    [Fact]
    public async Task Wire_UnbuiltIndex_SessionOutsideRoot_AnchorsTheRootAndNeverMentionsInit()
    {
        var root = UnbuiltWorkspace("ollama");
        var session = TestDirs.NewTempDir();
        await using var harness = await McpTestHarness.StartAsync(root, sessionDirectory: session);

        var payload = await CallUnbuiltIndexAsync(harness, "symbol_lookup", ("name", "Add"));

        var steps = Steps(payload);
        steps.ShouldContain($"cd '{root}' && squeezr index");
        payload.ToString().ShouldNotContain(session);
        payload.ToString().ShouldNotContain("squeezr init");
        payload.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(root));
    }

    [Fact]
    public async Task Wire_UnbuiltIndex_TextBlockStaysTheVerbatimSentence()
    {
        var root = UnbuiltWorkspace("ollama");
        await using var harness = await McpTestHarness.StartAsync(root);

        var result = await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Add" },
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text.ShouldBe(Sentence);
    }

    [Fact]
    public async Task Wire_UnbuiltIndex_RemedyUsesTheStartupFactAndNotTheConfigAsItStandsNow()
    {
        var root = UnbuiltWorkspace("ollama");
        await using var harness = await McpTestHarness.StartAsync(root);

        ConfigFile.Save(root, ConfigFile.Load(root) with
        {
            Embeddings = new EmbeddingsConfig { Provider = "openai" }
        });
        var payload = await CallUnbuiltIndexAsync(harness, "symbol_lookup", ("name", "Add"));

        Steps(payload).ShouldContain("squeezr services up");

        ConfigFile.Save(root, ConfigFile.Load(root) with
        {
            Embeddings = new EmbeddingsConfig { Provider = "ollama" }
        });
        var again = await CallUnbuiltIndexAsync(harness, "symbol_lookup", ("name", "Add"));

        Steps(again).ShouldContain("squeezr services up");
    }

    private static async Task<JsonElement> CallUnbuiltIndexAsync(
        McpTestHarness harness, string tool, (string Key, object? Value) argument)
    {
        var result = await harness.Client.CallToolAsync(
            tool,
            new Dictionary<string, object?> { [argument.Key] = argument.Value },
            cancellationToken: CancellationToken.None);

        return result.StructuredContent.ShouldNotBeNull();
    }

    private static string UnbuiltWorkspace(string provider)
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init(null);
        ConfigFile.Save(root, ConfigFile.Load(root) with
        {
            Embeddings = new EmbeddingsConfig { Provider = provider }
        });
        return root;
    }

    private static List<string> Steps(JsonElement payload) =>
        payload.GetProperty("nextSteps").EnumerateArray().Select(step => step.GetString()!).ToList();

    private static bool HasCause(JsonElement payload) =>
        payload.TryGetProperty("cause", out var cause) && cause.ValueKind != JsonValueKind.Null;
}
