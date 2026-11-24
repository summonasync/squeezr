using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Cli.Mcp.Tools;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

public class McpConcurrencyTests
{
    [Fact]
    public async Task ParallelToolCalls_AllSucceedWithoutMetaDbLock()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var calls = new (string Tool, Dictionary<string, object?> Args)[]
        {
            ("code_search", new() { ["query"] = "uniqneedle" }),
            ("index_status", []),
            ("find_references", new() { ["symbol"] = "Alpha" }),
            ("symbol_lookup", new() { ["name"] = "Alpha" }),
            ("repo_map", []),
            ("expand_span", new() { ["file"] = "a.cs", ["startLine"] = 1, ["endLine"] = 1 })
        };

        var results = await Task.WhenAll(calls.Select(c =>
            harness.Client.CallToolAsync(c.Tool, c.Args, cancellationToken: CancellationToken.None).AsTask()));

        foreach (var ((tool, _), result) in calls.Zip(results))
        {
            result.IsError.ShouldNotBe(true, $"tool {tool} failed: {string.Join(" ", result.Content.Select(b => b.ToString()))}");
        }

        var searchEnvelope = ParseEnvelope(results[0]);
        searchEnvelope.GetProperty("data").EnumerateArray().ShouldNotBeEmpty();
        var statusEnvelope = ParseEnvelope(results[1]);
        statusEnvelope.GetProperty("data").GetProperty("workspace").GetString().ShouldBe("demo");
        var refsEnvelope = ParseEnvelope(results[2]);
        refsEnvelope.GetProperty("data").EnumerateArray().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ParallelCodeSearchCalls_AllSucceed()
    {
        var workspace = NewIndexedWorkspace();
        await using var harness = await McpTestHarness.StartAsync(workspace);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            harness.Client.CallToolAsync(
                "code_search",
                new Dictionary<string, object?> { ["query"] = "uniqneedle" },
                cancellationToken: CancellationToken.None).AsTask()));

        foreach (var result in results)
        {
            result.IsError.ShouldNotBe(true);
            ParseEnvelope(result).GetProperty("data").EnumerateArray().ShouldNotBeEmpty();
        }
    }

    [Fact]
    public async Task QueuedSymbolLookup_CancelledWhileTheGateIsHeld_ThrowsInsteadOfEventuallyRunning()
    {
        var workspace = NewIndexedWorkspace();
        var gate = new McpToolGate();
        var tool = new SymbolLookupTool(new McpWorkspace(workspace, workspace, null), gate, null, new ClientIdentity());

        var insideGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = gate.RunAsync(async _ =>
        {
            insideGate.SetResult();
            await releaseGate.Task;
            return ToolEnvelope.Wrap(new { hit = 0 }, 1, false);
        }, CancellationToken.None);
        await insideGate.Task;

        using var cts = new CancellationTokenSource();
        var queued = tool.FindAsync(name: "Alpha", cancellationToken: cts.Token);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => queued);

        releaseGate.SetResult();
        await holder;
    }

    [Fact]
    public async Task QueuedGateCall_CancelledWhileWaiting_IsCancelledBeforeItsBodyRuns()
    {
        var gate = new McpToolGate();
        var insideGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = gate.RunAsync(async _ =>
        {
            insideGate.SetResult();
            await releaseGate.Task;
            return ToolEnvelope.Wrap(new { hit = 0 }, 1, false);
        }, CancellationToken.None);
        await insideGate.Task;

        using var cts = new CancellationTokenSource();
        var queued = gate.RunAsync(
            _ => Task.FromResult<object>(ToolEnvelope.Wrap(new { hit = 1 }, 1, false)),
            cts.Token);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => queued);

        releaseGate.SetResult();
        await holder;
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
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; void Run() { } }");
        WriteFile(workspace, "repo/b.cs", "class Beta { Alpha Make() { return new Alpha(); } }");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
