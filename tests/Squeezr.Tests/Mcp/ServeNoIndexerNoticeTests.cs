using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

// The honesty half of flipping the registration default (070/T5): a plain serve on a store nobody watches is
// the new normal, and it must say so — once to the agent in its first answer, once to the operator on stderr —
// while still answering every query correctly through per-query freshness.
public sealed class ServeNoIndexerNoticeTests
{
    private static readonly TimeSpan StartupLineTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task PlainServe_OnUnwatchedStore_SaysItOnStartup_AndOnceInFirstAnswer()
    {
        var workspace = NewIndexedWorkspace();
        var stderr = new LineCapture();

        await using var harness = await McpTestHarness.StartAsync(workspace, watch: false, stderr: stderr);

        var startup = await stderr.AwaitFor("no background indexer", StartupLineTimeout);
        startup.ShouldContain("queries refresh only what they touch");
        startup.ShouldContain("squeezr watch");

        var warnings = await WarningsAsync(harness);
        warnings.ShouldContain(w => w.Contains("no background indexer is running on this store"));

        // honest means correct too, and honest about its edge: a term nothing ever indexed stays invisible until a
        // query routes its changed file through the freshness path — after that the session answers from disk
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; string Note = \"plainidx7\"; }");
        (await HasHitAsync(harness, "plainidx7")).ShouldBeFalse(
            "a cold term surfaced without a pass would prove a background indexer this store does not have");
        (await HasHitAsync(harness, "uniqneedle")).ShouldBeTrue("the non-watcher lost the query path");
        (await HasHitAsync(harness, "plainidx7")).ShouldBeTrue(
            "query-path freshness did not run on the plain serve");
    }

    [Fact]
    public async Task PlainServe_WhileAWatcherOwnsTheStore_StaysQuiet()
    {
        var workspace = NewIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var claim = new WatchOwnerClaim(storeDir, DaemonHeartbeat.DefaultKind);
        claim.TryAcquire().IsOwner.ShouldBeTrue("test failed to hold the owner claim the scenario needs");
        var stderr = new LineCapture();

        try
        {
            await using var harness = await McpTestHarness.StartAsync(workspace, watch: false, stderr: stderr);

            var envelope = await FirstCallEnvelopeAsync(harness);
            envelope.GetProperty("warnings").EnumerateArray()
                .Select(w => w.GetString() ?? string.Empty)
                .ShouldNotContain(w => w.Contains("no background indexer", StringComparison.Ordinal),
                    "a session that merely does not index has nothing to apologize for while a watcher runs");
            stderr.Lines.ShouldNotContain(l => l.Contains("no background indexer"));
        }
        finally
        {
            claim.Release();
        }
    }

    private static async Task<IReadOnlyList<string>> WarningsAsync(McpTestHarness harness)
    {
        var envelope = await FirstCallEnvelopeAsync(harness);
        return envelope.GetProperty("warnings")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static async Task<JsonElement> FirstCallEnvelopeAsync(McpTestHarness harness)
    {
        var result = await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(
            true, $"index_status failed: {string.Join(" ", result.Content.Select(b => b.ToString()))}");
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static async Task<bool> HasHitAsync(McpTestHarness harness, string term)
    {
        var result = await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = term },
            cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(
            true, $"code_search '{term}' failed: {string.Join(" ", result.Content.Select(b => b.ToString()))}");
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.GetProperty("data").EnumerateArray().Any();
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { Enabled = false, DebounceSeconds = 1 }
        });
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"uniqneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed class LineCapture : StringWriter
    {
        private readonly object _sync = new();

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                base.WriteLine(value);
            }
        }

        public string[] Lines
        {
            get
            {
                lock (_sync)
                {
                    return ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }
            }
        }

        public async Task<string> AwaitFor(string needle, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var hit = Lines.FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal));
                if (hit is not null)
                {
                    return hit;
                }

                await Task.Delay(50);
            }

            throw new InvalidOperationException(
                $"stderr never said '{needle}' within {timeout.TotalSeconds}s; it said:{Environment.NewLine}{string.Join(Environment.NewLine, Lines)}");
        }
    }
}
