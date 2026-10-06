using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Cli.Mcp;

namespace Squeezr.Tests.Mcp;

public class McpServerSmokeTests
{
    [Fact]
    public async Task Initialize_ReturnsSqueezrServerInfo()
    {
        await using var harness = await McpTestHarness.StartAsync(TestDirs.NewTempDir());

        harness.Client.ServerInfo.Name.ShouldBe("squeezr");
        harness.Client.ServerInfo.Version.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ListTools_ReturnsRegisteredTools()
    {
        await using var harness = await McpTestHarness.StartAsync(TestDirs.NewTempDir());

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);

        tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(["code_search", "expand_span", "find_references", "index_status", "repo_map", "symbol_lookup"]);
    }

    [Fact]
    public async Task CallUnknownTool_ProtocolError_ServerStaysAlive()
    {
        await using var harness = await McpTestHarness.StartAsync(TestDirs.NewTempDir());

        await Should.ThrowAsync<McpException>(
            () => harness.Client.CallToolAsync(
                "no-such-tool",
                new Dictionary<string, object?>(),
                cancellationToken: CancellationToken.None).AsTask());

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        tools.Count.ShouldBe(6);
    }

    [Fact]
    public async Task ToolCallOutsideWorkspace_ReturnsStructuredError_ServerStaysAlive()
    {
        await using var harness = await McpTestHarness.StartAsync(TestDirs.NewTempDir());

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("not initialized");

        var tools = await harness.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        tools.Count.ShouldBe(6);
    }

    [Fact]
    public async Task ToolCallOutsideWorkspace_StructuredBlockCarriesCodeAndRemedy()
    {
        var root = TestDirs.NewTempDir();
        await using var harness = await McpTestHarness.StartAsync(root);

        var result = await harness.Client.CallToolAsync(
            "index_status",
            new Dictionary<string, object?>(),
            cancellationToken: CancellationToken.None);

        var payload = result.StructuredContent.ShouldNotBeNull();
        payload.GetProperty("code").GetString().ShouldBe("workspace_not_initialized");
        payload.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(root));
        payload.GetProperty("nextSteps").GetArrayLength().ShouldBeGreaterThan(0);

        result.IsError.ShouldBe(true);
        result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text
            .ShouldBe("Workspace is not initialized. Run 'squeezr init' first.");
    }

    [Fact]
    public async Task RunAsync_Cancellation_CompletesGracefully()
    {
        var toServer = new System.IO.Pipelines.Pipe();
        var fromServer = new System.IO.Pipelines.Pipe();
        using var cts = new CancellationTokenSource();

        var serverTask = McpServerHost.RunAsync(
            toServer.Reader.AsStream(),
            fromServer.Writer.AsStream(),
            TestDirs.NewTempDir(),
            cts.Token);

        cts.Cancel();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
