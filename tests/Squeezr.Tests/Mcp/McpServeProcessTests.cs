using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Storage;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Mcp;

[Collection("ProcessGlobals")]
public sealed class McpServeProcessTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    [Fact]
    public async Task Serve_PipedSession_EveryStdoutLineIsValidJsonRpc()
    {
        var root = TestDirs.NewTempDir();

        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" serve")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)!;
        await process.StandardInput.WriteLineAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"purity-test","version":"1.0"}}}
            """);
        await process.StandardInput.WriteLineAsync("""
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            """);
        await process.StandardInput.WriteLineAsync("""
            {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
            """);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestTimeout());
        var stdOut = await stdoutTask;
        var stdErr = await stderrTask;

        process.ExitCode.ShouldBe(0);
        stdErr.ShouldContain("squeezr serve");

        var lines = stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Length.ShouldBeGreaterThanOrEqualTo(2);

        var sawInitializeResult = false;
        var sawToolsList = false;
        foreach (var line in lines)
        {
            line.Contains('\u001b').ShouldBeFalse();
            using var doc = JsonDocument.Parse(line);
            var message = doc.RootElement;
            message.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
            var id = message.GetProperty("id").GetInt32();
            var result = message.GetProperty("result");
            if (id == 1)
            {
                sawInitializeResult = result.TryGetProperty("capabilities", out _);
            }
            else if (id == 2)
            {
                sawToolsList = result.GetProperty("tools").EnumerateArray().Count() == 7;
            }
        }

        sawInitializeResult.ShouldBeTrue();
        sawToolsList.ShouldBeTrue();
    }

    [Fact]
    public async Task Serve_MapAnswer_WiresFreshnessLast_WithTheOldKeysUntouched()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        (await CliTestProcess.RunWithHomeAsync(home, workspace, "init", "--name", "freshwire", "--json"))
            .ExitCode.ShouldBe(0);
        // no embedding rewiring here: this pin reads the envelope's bytes, and an answer shape does not
        // depend on whether the vector arm of the index came up
        Directory.CreateDirectory(Path.Combine(workspace, "repo", "src"));
        File.WriteAllText(Path.Combine(workspace, "repo", "src", "Alpha.cs"), "public class Alpha { public int Value = 1; }\n");
        File.WriteAllText(Path.Combine(workspace, "repo", "src", "Gone.cs"), "public class Gone { public int Value = 1; }\n");
        (await CliTestProcess.RunWithHomeAsync(home, workspace, "add", "repo", "--name", "demo", "--json"))
            .ExitCode.ShouldBe(0);
        (await CliTestProcess.RunWithHomeAsync(home, workspace, "index", "--json")).ExitCode.ShouldBe(0);
        File.Delete(Path.Combine(workspace, "repo", "src", "Gone.cs"));

        var (stdout, _) = await ServeToolCallAsync(home, workspace, workspace,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"repo_map","arguments":{"depth":2}}}""");

        // raw bytes off the pipe: the field is additive, sits last, and moves no existing name, type or order
        var answer = ToolCallResponse(stdout);
        answer.ShouldContain(@"\u0022freshness\u0022:");

        using var message = JsonDocument.Parse(answer);
        using var envelope = JsonDocument.Parse(message.RootElement
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        var root = envelope.RootElement;

        root.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["data", "tokensUsed", "truncated", "warnings", "indexAge", "staleFiles", "cost", "freshness"]);
        root.GetProperty("staleFiles").ValueKind.ShouldBe(JsonValueKind.Array);
        root.GetProperty("staleFiles").EnumerateArray().Select(v => v.GetString())
            .ShouldBe(["demo/src/Gone.cs"]);
        root.GetProperty("tokensUsed").ValueKind.ShouldBe(JsonValueKind.Number);
        root.GetProperty("indexAge").ValueKind.ShouldNotBe(JsonValueKind.Null);
        root.GetProperty("cost").EnumerateObject().Select(p => p.Name)
            .ShouldBe(["tokensUsed", "complete"]);
        root.GetProperty("freshness").EnumerateObject().Select(p => p.Name).ShouldBe(
            ["check", "checked", "scopeRows", "staleTotal", "listed", "listedMissing", "missing", "changed",
                "freshnessTokens"]);
        root.GetProperty("freshness").GetProperty("check").GetString().ShouldBe("structure");
        root.GetProperty("freshness").GetProperty("scopeRows").GetInt32().ShouldBe(2);
        root.GetProperty("freshness").GetProperty("staleTotal").GetInt32().ShouldBe(1);
        // the verdict is named whole here, so the answer keeps its completeness claim
        root.GetProperty("cost").GetProperty("complete").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Serve_MapReplayInOneSession_MarksTheAnswerThatRanNoCheck()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        (await CliTestProcess.RunWithHomeAsync(home, workspace, "init", "--name", "replaywire", "--json"))
            .ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(workspace, "repo", "src"));
        File.WriteAllText(Path.Combine(workspace, "repo", "src", "Alpha.cs"), "public class Alpha { public int Value = 1; }\n");
        File.WriteAllText(Path.Combine(workspace, "repo", "src", "Gone.cs"), "public class Gone { public int Value = 1; }\n");
        (await CliTestProcess.RunWithHomeAsync(home, workspace, "add", "repo", "--name", "demo", "--json"))
            .ExitCode.ShouldBe(0);
        (await CliTestProcess.RunWithHomeAsync(home, workspace, "index", "--json")).ExitCode.ShouldBe(0);

        var stdout = await ServeReplayAsync(home, workspace, MapCall(2),
            () => File.Delete(Path.Combine(workspace, "repo", "src", "Gone.cs")),
            MapCall(3));

        // the reviewer's B3 measurement as a committed pin: one session, one file gone between two identical
        // calls, and the cache keeps answering with numbers the second call never earned
        using var first = JsonDocument.Parse(EnvelopeJson(ToolCallResponse(stdout, 2)));
        using var second = JsonDocument.Parse(EnvelopeJson(ToolCallResponse(stdout, 3)));
        var fresh = first.RootElement.GetProperty("freshness");
        var replay = second.RootElement.GetProperty("freshness");

        fresh.TryGetProperty("replayed", out _).ShouldBeFalse();
        Int(fresh, "staleTotal").ShouldBe(0);
        replay.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        Int(replay, "staleTotal").ShouldBe(0,
            "the disk lost a row after the stored answer, and only the mark says this call never checked");
        replay.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["check", "checked", "scopeRows", "staleTotal", "listed", "listedMissing", "missing", "changed",
                "freshnessTokens", "replayed"]);
        Int(replay, "freshnessTokens").ShouldBeGreaterThan(Int(fresh, "freshnessTokens"),
            "the mark itself ships bytes, and the declared price moves with them");
        second.RootElement.GetProperty("warnings").EnumerateArray().ShouldBeEmpty(
            "a replay adds no prose warning — the mark is the whole notice");
    }

    private static int Int(JsonElement element, string key) => element.GetProperty(key).GetInt32();

    private static string MapCall(int id) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id +
        ",\"method\":\"tools/call\",\"params\":{\"name\":\"repo_map\",\"arguments\":{\"depth\":2}}}";

    private static string EnvelopeJson(string responseLine)
    {
        using var message = JsonDocument.Parse(responseLine);
        return message.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static async Task<string> ServeReplayAsync(
        string squeezrHome, string workingDir, string firstCall, Action betweenCalls, string secondCall)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" serve")
        {
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["SQUEEZR_HOME"] = squeezrHome;
        startInfo.Environment["CLAUDE_PROJECT_DIR"] = workingDir;
        startInfo.Environment["NO_COLOR"] = "1";

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var lines = new List<string>();

        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"replay-wire","version":"0"}}}""");
        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await process.StandardInput.FlushAsync();
        await process.StandardInput.WriteLineAsync(firstCall);
        await process.StandardInput.FlushAsync();

        while (await process.StandardOutput.ReadLineAsync(TestTimeout()) is { } line)
        {
            lines.Add(line);
            if (line.Contains("\"id\":2"))
            {
                break;
            }
        }

        betweenCalls();
        await process.StandardInput.WriteLineAsync(secondCall);
        await process.StandardInput.FlushAsync();

        while (await process.StandardOutput.ReadLineAsync(TestTimeout()) is { } line)
        {
            lines.Add(line);
            if (line.Contains("\"id\":3"))
            {
                break;
            }
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync(TestTimeout());
        await stderr;
        return string.Join('\n', lines);
    }

    [Fact]
    public async Task Serve_SessionOutsideResolvedRoot_EnvelopeRebindsAndStderrWarningStays()
    {
        var home = TestDirs.NewTempDir();
        var registered = TestDirs.NewTempDir();
        var session = TestDirs.NewTempDir();

        await CliTestProcess.RunWithHomeAsync(home, registered, "init", "--name", "wire-root");
        File.Delete(Directory.GetFiles(
            Path.Combine(home, "workspaces"), "workspace.json", SearchOption.AllDirectories).Single());

        var (stdout, stderr) = await ServeToolCallAsync(home, session, registered,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"index_status","arguments":{}}}""");

        var answer = ToolCallResponse(stdout);
        answer.ShouldContain("\"code\":\"workspace_not_initialized\"");
        answer.ShouldContain("\"cause\":\"session_outside_root\"");
        answer.ShouldContain(Path.GetFullPath(registered));
        answer.ShouldNotContain("squeezr init");
        answer.ShouldContain("squeezr mcp install");
        answer.ShouldContain("Do not initialize a store");

        stderr.ShouldContain("is outside the workspace root");
        stderr.ShouldContain("is not an initialized workspace");
    }

    [Fact]
    public async Task Serve_SessionAtUnresolvedRoot_EnvelopePrescribesInit()
    {
        var home = TestDirs.NewTempDir();
        var session = TestDirs.NewTempDir();

        var (stdout, stderr) = await ServeToolCallAsync(home, session, null,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"index_status","arguments":{}}}""");

        var answer = ToolCallResponse(stdout);
        answer.ShouldContain("\"cause\":\"root_without_manifest\"");
        answer.ShouldContain("squeezr init");
        answer.ShouldContain("squeezr index");
        answer.ShouldContain(Path.GetFullPath(session));
        answer.ShouldNotContain("squeezr mcp install");

        stderr.ShouldNotContain("is outside the workspace root");
    }

    private static async Task<(string StdOut, string StdErr)> ServeToolCallAsync(
        string squeezrHome, string workingDir, string? claudeProjectDir, string toolCall)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" serve")
        {
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["SQUEEZR_HOME"] = squeezrHome;
        startInfo.Environment["CLAUDE_PROJECT_DIR"] = claudeProjectDir ?? string.Empty;
        startInfo.Environment["NO_COLOR"] = "1";

        using var process = Process.Start(startInfo)!;
        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"cause-probe","version":"0"}}}""");
        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await process.StandardInput.WriteLineAsync(toolCall);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestTimeout());
        return (stdout, stderr);
    }

    private static string ToolCallResponse(string stdout) => ToolCallResponse(stdout, 2);

    private static string ToolCallResponse(string stdout, int id) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(line => line.Contains($"\"id\":{id}"));

    // AC1 of 116/T11: the store serve binds comes from the cwd, and the startup line names which one it is and
    // which rule picked it. A registered worktree is the case the parent's index used to wear unnoticed.
    [Fact]
    public async Task Serve_CwdIsARegisteredWorktree_BindsTheChildStoreAndSaysSoOnTheBindingLine()
    {
        var home = TestDirs.NewTempDir();
        var main = await IndexedCheckoutAsync(home, "wire-bind");
        var lane = Path.Combine(main, ".lanes", "116-serve-bind");
        TestGitRepo.AddWorktree(main, lane, "feat/116-serve-bind");
        // --reindex keeps this pin off the vector arm entirely: nothing is copied, so no collection is named
        (await CliTestProcess.RunWithHomeAsync(home, lane, "worktree", "init", "--reindex", "--no-semantic", "--json"))
            .ExitCode.ShouldBe(0);

        var (stdout, stderr) = await ServeToolCallAsync(home, lane, null,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"index_status","arguments":{}}}""");

        stderr.ShouldContain(
            $"squeezr serve: workspace '{SessionLocation.Display(lane)}' store '{StoreKeyOf(home, lane)}' " +
            "(this directory is a registered workspace root)");
        // a checkout that owns a store is not told to mint one
        stderr.ShouldNotContain("has no store of its own");

        using var envelope = JsonDocument.Parse(EnvelopeJson(ToolCallResponse(stdout)));
        envelope.RootElement.GetProperty("data").GetProperty("rootPath").GetString()
            .ShouldBe(SessionLocation.Display(lane),
                "the answer names the lane tree, not the main worktree it was copied from");
    }

    // W-3: a refusal is an answer about a tree too, and the gate that stamps envelopes skips this payload shape,
    // so the naming is measured on the bytes of a session that stands in one checkout while another store answers.
    // The symbol is branch-only by construction: the dogfood that opened 116 was a lane told its own code was
    // missing, and the sentence it was told named nobody.
    [Fact]
    public async Task Serve_CrossedSession_BranchOnlySymbolRefusalNamesTheAnsweringStore()
    {
        var home = TestDirs.NewTempDir();
        var main = await IndexedCheckoutAsync(home, "wire-cross");
        var lane = Path.Combine(main, ".lanes", "116-cross");
        TestGitRepo.AddWorktree(main, lane, "feat/116-cross");
        WriteBranchOnlySymbol(lane);

        var (stdout, stderr) = await ServeToolCallAsync(home, lane, null, FindReferencesCall(BranchOnlySymbol));

        var answer = ToolCallResponse(stdout);
        // the advice block is a nested JSON object on the wire, so its names are measured as raw bytes
        answer.ShouldContain("\"code\":\"workspace_error\"");
        answer.ShouldContain("\"subject\":{");
        answer.ShouldContain($"\"rootPath\":\"{SessionLocation.Display(main)}\"");
        // which index refused, or the reader has two trees to guess between
        answer.ShouldContain($"\"storeKey\":\"{StoreKeyOf(home, main)}\"");
        RefusalText(answer).ShouldBe(
            $"No symbol named '{BranchOnlySymbol}' in the index. Run 'squeezr index' if the code changed.",
            "the sentence the CLI door has always said, unchanged: the naming rides beside it, not inside it");

        stderr.ShouldContain("has no store of its own");
        stderr.ShouldContain("squeezr worktree init");
    }

    // The control and the other half of the additive rule (035): the same symbol, refused by the same store for
    // a session that stands in that store's own root, keeps the bytes it always had — no subject key.
    [Fact]
    public async Task Serve_SessionAtTheAnsweringRoot_SameRefusalCarriesNoSubjectKey()
    {
        var home = TestDirs.NewTempDir();
        var main = await IndexedCheckoutAsync(home, "wire-plain");
        var lane = Path.Combine(main, ".lanes", "116-plain");
        TestGitRepo.AddWorktree(main, lane, "feat/116-plain");
        WriteBranchOnlySymbol(lane);

        var (stdout, _) = await ServeToolCallAsync(home, main, null, FindReferencesCall(BranchOnlySymbol));

        var answer = ToolCallResponse(stdout);
        RefusalText(answer).ShouldStartWith($"No symbol named '{BranchOnlySymbol}' in the index");
        answer.ShouldNotContain("\"subject\"");
    }

    // the refusal's own sentence, read off the content block the agent actually sees
    private static string RefusalText(string answerLine)
    {
        using var message = JsonDocument.Parse(answerLine);
        return message.RootElement.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString()!;
    }

    private const string BranchOnlySymbol = "LaneOnlyCoordinator";

    private static void WriteBranchOnlySymbol(string lane) =>
        File.WriteAllText(
            Path.Combine(lane, "repo", "Lane.cs"),
            $"public class {BranchOnlySymbol} {{ public void Run() {{ }} }}\n");

    // three opening braces (message, params, arguments), so the tail closes exactly three
    private static string FindReferencesCall(string symbol) =>
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":" +
        "{\"name\":\"find_references\",\"arguments\":{\"symbol\":\"" + symbol + "\"}}}";

    // --no-semantic keeps the fixture off the embedding arm entirely: the not-found leg needs rows, not vectors.
    private static async Task<string> IndexedCheckoutAsync(string squeezrHome, string name)
    {
        var root = TestDirs.NewTempDir();
        TestGitRepo.Init(root);
        Directory.CreateDirectory(Path.Combine(root, "repo"));
        File.WriteAllText(
            Path.Combine(root, "repo", "Alpha.cs"),
            "public class Alpha { public string Term = \"uniqneedle\"; }\n");
        TestGitRepo.Add(root, "repo/Alpha.cs");
        TestGitRepo.Commit(root, "seed");
        (await CliTestProcess.RunWithHomeAsync(squeezrHome, root, "init", "--name", name, "--json"))
            .ExitCode.ShouldBe(0);
        (await CliTestProcess.RunWithHomeAsync(squeezrHome, root, "add", "repo", "--name", "demo", "--json"))
            .ExitCode.ShouldBe(0);
        (await CliTestProcess.RunWithHomeAsync(squeezrHome, root, "index", "--no-semantic", "--json"))
            .ExitCode.ShouldBe(0);
        return root;
    }

    private static string StoreKeyOf(string squeezrHome, string rootPath)
    {
        var canonical = WorkspaceCanonicalPath.Canonicalize(rootPath);
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(squeezrHome, SqueezrPaths.RegistryFileName)));
        return doc.RootElement.EnumerateArray()
            .Single(row => row.GetProperty("rootPath").GetString() == canonical)
            .GetProperty("storeKey")
            .GetString()!;
    }

    private static CancellationToken TestTimeout() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;
}
