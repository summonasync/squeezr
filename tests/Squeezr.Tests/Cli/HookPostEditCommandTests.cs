using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

// The door shape, the payload fixtures and the pinned-home harness mirror HookPreReadCommandTests:
// every case runs the real CLI child so the fail-open contract is measured at the process exit,
// not at the Evaluate method.
[Collection("ProcessGlobals")]
public sealed class HookPostEditCommandTests : IAsyncLifetime
{
    private readonly List<Process> _children = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var child in _children)
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
            }

            child.Dispose();
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task EditedNewFile_TriggersOnePass_TheFileBecomesSearchable()
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var fresh = Write(repo, "Fresh.cs", "class HookProbe { const string ZedCanary77 = \"x\"; }\n");

        var hook = await CliTestProcess.RunPipedAsync(home, EditPayload("edit", fresh), root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, "ZedCanary77")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("write_file")]
    public async Task DeclaredEditToolNames_IndexTheirFile(string toolName)
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var file = Write(repo, $"Note{toolName.Length}.cs", $"class Note{toolName.Length} {{ int Toks{toolName.Length} = 0; }}\n");

        var hook = await CliTestProcess.RunPipedAsync(home, EditPayload(toolName, file), root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, $"Toks{toolName.Length}")).ShouldBeTrue();
    }

    [Fact]
    public async Task MultiEditNestedFragmentPath_IndexesThatFile()
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var file = Write(repo, "Fragment.cs", "class Fragment { string MultiCanary = \"\"; }\n");
        var payload = "{\"tool_name\":\"MultiEdit\",\"tool_input\":{\"edits\":[{\"old_string\":\"a\"," +
                      "\"new_string\":\"b\",\"file_path\":\"" + file + "\"}]}}";

        var hook = await CliTestProcess.RunPipedAsync(home, payload, root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, "MultiCanary")).ShouldBeTrue();
    }

    [Fact]
    public async Task NotebookPathMember_IndexesTheNotebook()
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var notebook = Write(repo, "nb.ipynb",
            "{\"cells\": [{\"source\": [\"NbCellCanary88\"]}], \"metadata\": {}, \"nbformat\": 4," +
            " \"nbformat_minor\": 5}\n");
        var payload = "{\"tool_name\":\"notebook_edit\",\"tool_input\":{\"notebook_path\":\"" + notebook + "\"}}";

        var hook = await CliTestProcess.RunPipedAsync(home, payload, root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, "NbCellCanary88")).ShouldBeTrue();
    }

    [Fact]
    public async Task ReadToolPayload_LeavesTheNewFileUnindexed()
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var file = Write(repo, "Quiet.cs", "class Quiet { int NoEntry = 0; }\n");

        var hook = await CliTestProcess.RunPipedAsync(home, EditPayload("read_file", file), root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, "NoEntry")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("[{\"tool_name\":\"edit\"}]")]
    [InlineData("{\"tool_name\":\"edit\"}")]
    [InlineData("{\"tool_name\":\"edit\",\"tool_input\":\"string\"}")]
    [InlineData("{\"tool_name\":\"edit\",\"tool_input\":{\"file_path\":\"\"}}")]
    [InlineData("{\"tool_name\":\"edit\",\"tool_input\":{\"file_path\":null}}")]
    public async Task MalformedOrEmptyPayload_FailsOpenSilently(string payload)
    {
        var (home, root, _) = await NewWorkspaceWithRepoAsync();

        var hook = await CliTestProcess.RunPipedAsync(home, payload, root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task TruncatedPayload_FailsOpenSilently()
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var file = Write(repo, "Cut.cs", "class Cut { int CutToken = 0; }\n");
        var complete = EditPayload("edit", file);

        var hook = await CliTestProcess.RunPipedAsync(home, complete[..(complete.Length - 2)], root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, "CutToken")).ShouldBeFalse();
    }

    [Fact]
    public async Task OversizedStdin_FailsOpenSilently()
    {
        var (home, root, _) = await NewWorkspaceWithRepoAsync();

        var hook = await CliTestProcess.RunPipedAsync(home, new string('x', 200 * 1024), root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task OversizedStdin_WithRealPathBeyondCap_FailsOpen()
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var file = Write(repo, "Bloat.cs", "class Bloat { int BloatToken = 0; }\n");

        var hook = await CliTestProcess.RunPipedAsync(
            home, EditPayload("edit", file) + new string(' ', 70 * 1024), root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, "BloatToken")).ShouldBeFalse();
    }

    [Fact]
    public async Task NoWorkspaceCwd_AnswersLikeTheSilentDoorItIs()
    {
        var home = TestDirs.NewTempDir();
        var bare = TestDirs.NewTempDir();
        var file = Write(bare, "Stray.cs", "class Stray { int StrayToken = 0; }\n");

        var hook = await CliTestProcess.RunPipedAsync(home, EditPayload("edit", file), bare, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task PathOutsideWorkspaceRoots_LeavesTheStoreAlone()
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var outside = TestDirs.NewTempDir();
        var outsideFile = Write(outside, "Alien.cs", "class Alien { int AlienToken = 0; }\n");

        var hook = await CliTestProcess.RunPipedAsync(home, EditPayload("edit", outsideFile), root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        var search = await CliTestProcess.RunWithHomeAsync(home, root, "search", "AlienToken", "--json");
        search.ExitCode.ShouldBe(0);
        CountMatches(search.StdOut).ShouldBe(0);
        (await SearchFindsAsync(home, root, "AlienToken")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(DaemonHeartbeat.DefaultKind)]
    [InlineData(DaemonHeartbeat.ServeWatchKind)]
    public async Task LiveWatcherHeartbeatOfEitherKind_SkipsThePassEntirely(string kind)
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var file = Write(repo, "Skipped.cs", "class Skipped { int SkipToken1 = 0; }\n");
        WriteKindHeartbeat(SqueezrPaths.StoreDir(root), kind, Environment.ProcessId);

        var hook = await CliTestProcess.RunPipedAsync(home, EditPayload("edit", file), root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, "SkipToken1")).ShouldBeFalse();

        // the heartbeat was the only difference: with the beat gone the same payload indexes the file
        File.Delete(DaemonHeartbeat.PathFor(SqueezrPaths.StoreDir(root), kind));
        var control = await CliTestProcess.RunPipedAsync(home, EditPayload("edit", file), root, "hook", "post-edit");
        control.ExitCode.ShouldBe(0);
        (await SearchFindsAsync(home, root, "SkipToken1")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(DaemonHeartbeat.DefaultKind)]
    [InlineData(DaemonHeartbeat.ServeWatchKind)]
    public async Task StaleBeatOfEitherKind_ThePassRunsAnyway(string kind)
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var file = Write(repo, "Late.cs", "class Late { int LateToken1 = 0; }\n");
        WriteKindHeartbeat(
            SqueezrPaths.StoreDir(root), kind, Environment.ProcessId,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6));

        var hook = await CliTestProcess.RunPipedAsync(home, EditPayload("edit", file), root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, "LateToken1")).ShouldBeTrue();
    }

    [Fact]
    public async Task FreshBeatDeadPid_ThePassRuns()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var file = Write(repo, "Zombie.cs", "class Zombie { int ZombieToken = 0; }\n");
        var gonePid = StartDeadProcess();
        WriteKindHeartbeat(SqueezrPaths.StoreDir(root), DaemonHeartbeat.DefaultKind, gonePid);

        var hook = await CliTestProcess.RunPipedAsync(home, EditPayload("edit", file), root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        (await SearchFindsAsync(home, root, "ZombieToken")).ShouldBeTrue();
    }

    [Fact]
    public async Task StrangerHoldsTheIndexLock_GivesUpInsideItsBudget_Silently()
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var file = Write(repo, "Busy.cs", "class Busy { int BusyToken = 0; }\n");
        var lockPath = Path.Combine(SqueezrPaths.StoreDir(root), SqueezrPaths.LockFileName);
        File.WriteAllText(lockPath, Environment.ProcessId.ToString());
        try
        {
            var hook = await CliTestProcess.RunPipedAsync(home, EditPayload("edit", file), root, "hook", "post-edit");

            hook.ExitCode.ShouldBe(0);
            hook.StdOut.ShouldBeEmpty();
            (await SearchFindsAsync(home, root, "BusyToken")).ShouldBeFalse();
        }
        finally
        {
            File.Delete(lockPath);
        }

        // the give-up was the lock, not the door: the same payload lands once the lock is gone
        var control = await CliTestProcess.RunPipedAsync(home, EditPayload("edit", file), root, "hook", "post-edit");
        control.ExitCode.ShouldBe(0);
        (await SearchFindsAsync(home, root, "BusyToken")).ShouldBeTrue();
    }

    [Fact]
    public async Task EditToASecretGuardedPath_IndexesNothingAndNamesNothing()
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var secret = Write(repo, ".env", "API_KEY=PostEditEnvCanary42\n");
        var normal = Write(repo, "Normal.cs", "class Normal { int NormalToken = 0; }\n");
        // announced together, in one door call: the batch has to show it indexed the path it may read and
        // refused the one it may not. Before the scoped route this case relied on the walk sweeping the
        // neighbour; the door no longer walks, so the neighbour is announced too and the refusal still holds
        var payload = "{\"tool_name\":\"MultiEdit\",\"tool_input\":{\"edits\":[" +
                      $"{{\"file_path\":\"{secret}\"}},{{\"file_path\":\"{normal}\"}}]}}}}";

        var hook = await CliTestProcess.RunPipedAsync(home, payload, root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, "NormalToken")).ShouldBeTrue(
            "the pass itself ran through the ordinary ingestion ruleset");
        (await SearchFindsAsync(home, root, "PostEditEnvCanary42")).ShouldBeFalse();
        (await SearchFindsAsync(home, root, "API_KEY")).ShouldBeFalse();

        var status = await CliTestProcess.RunWithHomeAsync(home, root, "status", "--json");
        status.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        doc.RootElement.GetProperty("repos")[0].GetProperty("guard").GetProperty("withheld").GetInt32()
            .ShouldBe(1);
    }

    [Fact]
    public async Task DeletedFilePathAnnouncement_LandsAsARemoval()
    {
        var (home, root, repo) = await NewWorkspaceWithRepoAsync();
        var doomed = Write(repo, "Doomed.cs", "class Doomed { int DoomToken = 0; }\n");
        (await CliTestProcess.RunWithHomeAsync(home, root, "index", "--json")).ExitCode.ShouldBe(0);
        (await SearchFindsAsync(home, root, "DoomToken")).ShouldBeTrue();

        File.Delete(doomed);
        var hook = await CliTestProcess.RunPipedAsync(home, EditPayload("edit", doomed), root, "hook", "post-edit");

        hook.ExitCode.ShouldBe(0);
        hook.StdOut.ShouldBeEmpty();
        (await SearchFindsAsync(home, root, "DoomToken")).ShouldBeFalse();
        var status = await CliTestProcess.RunWithHomeAsync(home, root, "status", "--json");
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        doc.RootElement.GetProperty("repos")[0].GetProperty("files").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData("edit", true)]
    [InlineData("write_file", true)]
    [InlineData("notebook_edit", true)]
    [InlineData("Edit", true)]
    [InlineData("Write", true)]
    [InlineData("MultiEdit", true)]
    [InlineData("NotebookEdit", true)]
    [InlineData("read_file", false)]
    [InlineData("Read", false)]
    [InlineData("edit_file", false)]
    [InlineData("Edit ", false)]
    [InlineData("write", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEditTool_DeclaredOrOffName_AnswersAsDeclared(string? toolName, bool expected) =>
        HookPostEditToolNames.IsEditTool(toolName).ShouldBe(expected);

    [Fact]
    public void PayloadIdsFor_TheTwoArmableRuntimes_ListsDeclaredIdsInOrder()
    {
        HookPostEditToolNames.PayloadIdsFor("qwen")
            .ShouldBe(["edit", "write_file", "notebook_edit"]);
        HookPostEditToolNames.PayloadIdsFor("claude")
            .ShouldBe(["Edit", "Write", "MultiEdit", "NotebookEdit"]);
        HookPostEditToolNames.PayloadIdsFor("cursor").ShouldBeEmpty();
    }

    private static async Task<(string Home, string Root, string Repo)> NewWorkspaceWithRepoAsync()
    {
        // the shared test-run home, the same way the fleet heartbeat tests resolve it: the child and
        // this process then read one registry, so storeDir-shaped fixtures land where the CLI looks
        var home = SqueezrHome.Resolve();
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        var repo = Path.Combine(root, "code");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "App.cs"), "public class App { public void Run() { } }\n");
        (await CliTestProcess.RunAsync(root, "add", "code", "--name", "demo", "--json")).ExitCode.ShouldBe(0);
        (await CliTestProcess.RunAsync(root, "index", "--json")).ExitCode.ShouldBe(0);
        return (home, root, repo);
    }

    private static async Task<bool> SearchFindsAsync(string home, string root, string term)
    {
        var search = await CliTestProcess.RunWithHomeAsync(home, root, "search", term, "--json");
        return search.ExitCode == 0 && CountMatches(search.StdOut) > 0;
    }

    private static int CountMatches(string searchJson)
    {
        foreach (var line in searchJson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("matches", out var matches)
                && doc.RootElement.GetProperty("type").GetString() == "summary")
            {
                return matches.GetInt32();
            }
        }

        throw new InvalidOperationException($"search --json answered without a summary line: {searchJson}");
    }

    private static string EditPayload(string toolName, string filePath) =>
        "{\"tool_name\":\"" + toolName + "\",\"tool_input\":{\"file_path\":\"" + filePath + "\"}}";

    private static string Write(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static void WriteKindHeartbeat(
        string storeDir, string kind, int pid, TimeSpan? beatAge = null, TimeSpan? startedAge = null)
    {
        var now = DateTime.UtcNow;
        var beat = new DaemonHeartbeat(
            pid, kind, now - (startedAge ?? TimeSpan.FromMinutes(3)), now - (beatAge ?? TimeSpan.Zero));
        File.WriteAllText(
            DaemonHeartbeat.PathFor(storeDir, kind),
            JsonSerializer.Serialize(beat, SqueezrJson.FileOptions));
    }

    // Start something real, reap it deliberately, and the surviving number is the honest dead pid the
    // stale-threshold twin cannot fake: the beat stays fresh, only the process half of the oracle fails.
    private int StartDeadProcess()
    {
        var process = Process.Start(
            new ProcessStartInfo("/bin/sleep", "300") { UseShellExecute = false })!;
        var pid = process.Id;
        process.Kill();
        process.WaitForExit();
        process.Dispose();
        return pid;
    }
}
