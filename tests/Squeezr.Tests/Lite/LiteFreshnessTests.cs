using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Mcp;

namespace Squeezr.Tests.Lite;

public class LiteFreshnessTests : IDisposable
{
    private const string MinimalCalcSource = """
        namespace Demo;

        public class Calc
        {
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    private readonly string _workspace = TestDirs.NewTempDir();
    private string _home = TestDirs.NewTempDir();

    [Fact]
    public async Task CodeSearch_HealsEditBurstAcrossQueries_AndCountsTellHealedFromRefused()
    {
        NewIndexedWorkspace();
        WriteBurstFiles("original");
        new WorkspaceIndexer().Index(_workspace);
        await using var harness = await LiteMcpTestHarness.StartAsync(_workspace);

        var warmup = await CallSearch(harness);
        warmup.IsError.ShouldNotBe(true, Envelope(warmup).GetRawText());

        WriteBurstFiles("REWRITTEN");

        var first = Envelope(await CallSearch(harness));
        var block = first.GetProperty("freshness");
        block.GetProperty("check").GetString().ShouldBe("content");
        var refreshed = block.GetProperty("refreshed").GetInt32();
        var staleTotal = block.GetProperty("staleTotal").GetInt32();
        refreshed.ShouldBeGreaterThanOrEqualTo(1);
        refreshed.ShouldBeLessThanOrEqualTo(RefreshBudget.MaxFilesReRead);
        (refreshed + staleTotal).ShouldBe(block.GetProperty("checked").GetInt32());
        block.GetProperty("unrefreshedFiles").GetInt32().ShouldBe(staleTotal);
        first.GetProperty("staleFiles").EnumerateArray().Count().ShouldBe(staleTotal);

        var totalRefreshed = refreshed;
        var rounds = 0;
        while (block.GetProperty("staleTotal").GetInt32() > 0 && rounds++ < 10)
        {
            var next = Envelope(await CallSearch(harness));
            block = next.GetProperty("freshness");
            totalRefreshed += block.GetProperty("refreshed").GetInt32();
        }

        rounds.ShouldBeLessThan(10);
        block.GetProperty("staleTotal").GetInt32().ShouldBe(0);
        totalRefreshed.ShouldBe(8);
        Envelope(await CallSearch(harness)).GetProperty("staleFiles").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task SecondServeWatch_OnTheSameStore_RefusesIndexing_ThenTakesTheLoopWhenTheOwnerStepsAside()
    {
        NewIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(_workspace);
        var owner = new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind);
        owner.TryAcquire().IsOwner.ShouldBeTrue();

        var contenderClaim = new WatchOwnerClaim(
            storeDir, DaemonHeartbeat.ServeWatchKind, null, null, null, pid: 999_999);
        var stderr = new StringWriter();
        await using var contender = LiteServeWatch.For(
            _workspace, stderr, contenderClaim, TimeSpan.FromMilliseconds(50));
        await contender.StartAsync(CancellationToken.None);

        contender.IsIndexing.ShouldBe(false);
        stderr.ToString().ShouldContain($"held by pid {Environment.ProcessId}");
        stderr.ToString().ShouldContain("answers queries only");

        owner.Release().ShouldBeTrue();

        WaitFor(() => contender.IsIndexing, TimeSpan.FromSeconds(10))
            .ShouldBeTrue(stderr.ToString());
        stderr.ToString().ShouldContain("watch engine running");
    }

    [Fact]
    public async Task IndexCommand_SkipsWhileALiveWatcherOwnsTheStore_AndIndexesOnceItIsGone()
    {
        _home = TestDirs.NewTempDir();
        WriteFile("repo/src/Calc.cs", MinimalCalcSource);
        (await RunLiteAsync("init", "--json")).Code.ShouldBe(0);
        (await RunLiteAsync("add", "repo", "--name", "demo")).Code.ShouldBe(0);
        (await RunLiteAsync("index", "--json")).Code.ShouldBe(0);

        var storeDir = Directory.EnumerateFiles(_home, "meta.db", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .ShouldHaveSingleItem();
        using var beat = new DaemonHeartbeatWriter(storeDir!, DaemonHeartbeat.ServeWatchKind);
        beat.Start();

        var (skipCode, _, skipErr) = await RunLiteAsync("index", "--json");
        skipCode.ShouldBe(0, skipErr);
        skipErr.ShouldContain($"a watcher pass owns this store (pid {Environment.ProcessId})");

        beat.Dispose();
        var (indexCode, indexOut, indexErr) = await RunLiteAsync("index", "--json");
        indexCode.ShouldBe(0, indexErr);
        indexOut.ShouldContain("\"files\"");
    }

    [Fact]
    public async Task IndexStatus_OmitsStaleFiles_NeverShipsAFakeEmptyArray()
    {
        NewIndexedWorkspace();
        await using var harness = await LiteMcpTestHarness.StartAsync(_workspace);

        var envelope = Envelope(await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None));
        envelope.TryGetProperty("staleFiles", out _).ShouldBe(false);
        envelope.TryGetProperty("freshness", out _).ShouldBe(false);
        envelope.GetProperty("warnings").EnumerateArray()
            .Select(line => line.GetString())
            .ShouldContain(line => line!.Contains("no background indexer is running"));

        var claim = new WatchOwnerClaim(SqueezrPaths.StoreDir(_workspace), DaemonHeartbeat.ServeWatchKind);
        claim.TryAcquire().IsOwner.ShouldBeTrue();
        try
        {
            var owned = Envelope(await harness.Client.CallToolAsync(
                "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None));
            owned.GetProperty("warnings").EnumerateArray()
                .Select(line => line.GetString())
                .ShouldContain(line =>
                    line!.Contains("a watcher pass keeps this index fresh") &&
                    line.Contains($"pid {Environment.ProcessId}"));
        }
        finally
        {
            claim.Release();
        }
    }

    [Fact]
    public async Task SymbolLookup_RerunsFindAfterAnInlineHeal_AndShipsTheHealedRow()
    {
        NewIndexedWorkspace();
        await using var harness = await LiteMcpTestHarness.StartAsync(_workspace);

        WriteFile("repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers with a healed marker.</summary>
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);

        var envelope = Envelope(await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Add", ["maxTokens"] = 300 },
            cancellationToken: CancellationToken.None));

        envelope.GetProperty("freshness").GetProperty("refreshed").GetInt32()
            .ShouldBeGreaterThanOrEqualTo(1);
        envelope.GetProperty("data").EnumerateArray()
            .Select(hit => hit.GetProperty("docComment").GetString())
            .ShouldContain(doc => doc!.Contains("healed marker"));
    }

    [Fact]
    public async Task SymbolLookup_ShipsSuggestionsInDidYouMean_WhileStaleFilesStaysTheVerdictList()
    {
        NewIndexedWorkspace();
        await using var harness = await LiteMcpTestHarness.StartAsync(_workspace);

        var envelope = Envelope(await harness.Client.CallToolAsync(
            "symbol_lookup",
            new Dictionary<string, object?> { ["name"] = "Addd" },
            cancellationToken: CancellationToken.None));

        envelope.GetProperty("didYouMean").EnumerateArray()
            .Select(v => v.GetString())
            .ShouldContain(name => name == "Add");
        envelope.GetProperty("staleFiles").ValueKind.ShouldBe(JsonValueKind.Array);
        envelope.GetProperty("staleFiles").EnumerateArray()
            .Select(v => v.GetString())
            .ShouldNotContain(name => name == "Add");
        envelope.GetProperty("freshness").GetProperty("check").GetString().ShouldBe("content");
    }

    [Fact]
    public async Task RepoMap_ShipsTheStructureVerdictBlock_WithoutReindexing()
    {
        NewIndexedWorkspace();
        File.Delete(Path.Combine(_workspace, "repo", "src", "Runner.cs"));
        await using var harness = await LiteMcpTestHarness.StartAsync(_workspace);

        var envelope = Envelope(await harness.Client.CallToolAsync(
            "repo_map", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None));

        var block = envelope.GetProperty("freshness");
        block.GetProperty("check").GetString().ShouldBe("structure");
        block.TryGetProperty("refreshed", out _).ShouldBe(false);
        block.GetProperty("staleTotal").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        envelope.GetProperty("staleFiles").EnumerateArray()
            .Select(v => v.GetString())
            .ShouldContain(path => path!.EndsWith("Runner.cs"));
    }

    public void Dispose()
    {
        foreach (var leaf in new[] { _workspace, _home })
        {
            try
            {
                Directory.Delete(leaf, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void WriteBurstFiles(string marker)
    {
        for (var i = 0; i < 8; i++)
        {
            File.WriteAllText(
                Path.Combine(_workspace, "repo", "docs", $"note{i}.md"),
                $"# Note {i}\n\nfreshnessprobe token {marker} on file {i}.\n");
        }
    }

    private async Task<CallToolResult> CallSearch(LiteMcpTestHarness harness) =>
        await harness.Client.CallToolAsync(
            "code_search",
            new Dictionary<string, object?> { ["query"] = "freshnessprobe", ["topK"] = 20 },
            cancellationToken: CancellationToken.None);

    private static JsonElement Envelope(CallToolResult result)
    {
        var json = result.Content.ShouldHaveSingleItem().ShouldBeOfType<TextContentBlock>().Text;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    private async Task<(int Code, string StdOut, string StdErr)> RunLiteAsync(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "sqzrlite.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["SQUEEZR_HOME"] = _home;
        startInfo.Environment["SQUEEZR_CLIENT_HOME"] = TestDirs.SharedClientHomeDir;
        startInfo.Environment["DOTNET_CLI_HOME"] = TestDirs.SharedClientHomeDir;

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private void NewIndexedWorkspace()
    {
        var manager = new WorkspaceManager(_workspace);
        manager.Init("demo");
        WriteFile("repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);
        WriteFile("repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return new Calc().Add(1, 2);
                }
            }
            """);
        WriteFile("repo/docs/notes.md", "# Notes\n\nThe Calc type adds numbers.\n");
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(_workspace);
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
