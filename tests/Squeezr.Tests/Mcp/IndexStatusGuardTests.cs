using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Tests.Indexing;

namespace Squeezr.Tests.Mcp;

// index_status is one of the two ledgers the guard has to be visible in. It reports what a pass refused to
// take in; the read-side tally still comes per answer, so a caller that reads both sees the whole picture.
public class IndexStatusGuardTests
{
    [Fact]
    public async Task IndexStatus_AfterARefusingPass_LedgersTheGuardBlockWithoutNamingAPath()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithSecrets();
        new WorkspaceIndexer().Index(workspace);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var envelope = Parse(result);
        var payload = envelope.ToString();
        payload.ShouldNotContain("monkey.pem");
        payload.ShouldNotContain("\".env\"");

        var repo = envelope.GetProperty("data").GetProperty("repos").EnumerateArray().ShouldHaveSingleItem();
        repo.TryGetProperty("guard", out var block).ShouldBeTrue();
        var guard = block;
        guard.GetProperty("withheld").GetInt32().ShouldBe(2);
        guard.GetProperty("withheldByBuiltIns").GetInt32().ShouldBe(2);
        guard.GetProperty("dropped").GetInt32().ShouldBe(0);

        Warning(envelope).ShouldContain(
            "secret guard: 2 file(s) withheld from indexing (built-in patterns)");
    }

    [Fact]
    public async Task IndexStatus_WithAnOrdinaryStore_OmitsTheGuardBlock()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithPlainFile();
        new WorkspaceIndexer().Index(workspace);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var result = await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        var envelope = Parse(result);
        envelope.GetProperty("data").GetProperty("repos").EnumerateArray().ShouldHaveSingleItem()
            .TryGetProperty("guard", out _).ShouldBeFalse();
        Warning(envelope).ShouldNotContain("secret guard");
    }

    [Fact]
    public async Task IndexStatus_AndAReadAnswer_ShowBothSidesOfTheWithheldTally()
    {
        var workspace = IngestionWorkspaces.NewWorkspaceWithSecrets();
        new WorkspaceIndexer().Index(workspace);
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var status = Parse(await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None));
        var search = Parse(await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = IngestionWorkspaces.EnvCanary },
            cancellationToken: CancellationToken.None));

        Warning(status).ShouldContain("file(s) withheld from indexing");
        search.ToString().ShouldNotContain(IngestionWorkspaces.EnvCanary);
        search.ToString().ShouldNotContain("monkey.pem");
    }

    private static JsonElement Parse(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string Warning(JsonElement envelope) => string.Join(
        "|", envelope.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
}
