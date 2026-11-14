using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Cli;

// The hook body as an agent meets it: one stdin payload in, one SessionStart answer out. The lanes here are
// real linked checkouts and every run goes through the CLI, because the point of this surface is that the hook
// calls the verb — an in-process fake would test a decision the product never makes.
// Each lane store is pre-minted with dead provider and Qdrant endpoints and indexing.noSemantic, so no pass
// here names a real collection or reaches a local service.
[Collection("ProcessGlobals")]
public sealed class HookSessionStartCliTests
{
    [Fact]
    public async Task SessionStart_UnregisteredLane_RegistersItAndNamesTheNewStore()
    {
        var main = await IndexedMainAsync("hook-lane-main");
        var lane = Lane(main, "feat/116-hook");

        var run = await HookAsync(lane);

        run.ExitCode.ShouldBe(0);
        var context = ContextOf(run.StdOut);
        context.ShouldContain("got its own index just now");
        context.ShouldContain(StoreKeyOfLane(lane, "feat/116-hook"));
        context.ShouldContain("feat/116-hook");
        RowsNaming(lane).Length.ShouldBe(1);
    }

    [Fact]
    public async Task SessionStart_RunTwiceInOneLane_SecondIsANoOpReportWithOneRowAndOneStore()
    {
        var main = await IndexedMainAsync("hook-twice-main");
        var lane = Lane(main, "feat/116-twice");
        var storeKey = StoreKeyOfLane(lane, "feat/116-twice");

        var first = await HookAsync(lane);
        var before = StoreDirsUnderStoreKey(storeKey);
        var second = await HookAsync(lane);

        first.ExitCode.ShouldBe(0);
        second.ExitCode.ShouldBe(0);
        ContextOf(second.StdOut).ShouldContain("nothing was created this session");
        RowsNaming(lane).Length.ShouldBe(1, "two sessions, one row — a duplicate registry row is the fork this closes");
        StoreDirsUnderStoreKey(storeKey).ShouldBe(before, "the second pass created no store");
    }

    [Fact]
    public async Task SessionStart_ThreePasses_LeaveOneRowForTheLaneAndOneForTheParent()
    {
        var main = await IndexedMainAsync("hook-thrice-main");
        var lane = Lane(main, "feat/116-thrice");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            (await HookAsync(lane)).ExitCode.ShouldBe(0);
        }

        RowsNaming(lane).Length.ShouldBe(1);
        RowsNaming(main).Length.ShouldBe(1, "the session-start path must not breed a second row for the parent");
    }

    [Fact]
    public async Task SessionStart_InTheMainWorktree_AnswersWithSilence()
    {
        var main = await IndexedMainAsync("hook-main-silence");

        var run = await HookAsync(main);

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBeEmpty("a main checkout keeps the store it has; the hook has nothing to tell");
    }

    [Fact]
    public async Task SessionStart_OutsideAnyRepository_AnswersWithSilence()
    {
        var plain = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(plain, "readme.md"), "no git here\n");

        var run = await HookAsync(plain);

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionStart_LaneOfAnUnindexedRepository_AnswersWithSilenceAndRegistersNothing()
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "a.txt"), "a\n");
        TestGitRepo.Add(main, "a.txt");
        TestGitRepo.Commit(main, "seed");
        var lane = Lane(main, "feat/116-unindexed");

        var run = await HookAsync(lane);

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBeEmpty("the verb created nothing, so the session gets no squeezr sentence");
        RowsNaming(lane).ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionStart_MalformedPayload_AnswersWithSilenceAtExitZero()
    {
        var main = await IndexedMainAsync("hook-broken-main");
        var lane = Lane(main, "feat/116-broken");

        var run = await CliTestProcess.RunPipedInSharedHomeAsync("{not json", lane, "hook", "session-start");

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBeEmpty();
        RowsNaming(lane).ShouldBeEmpty("a payload this hook was not given is not a session to act on");
    }

    [Fact]
    public async Task SessionStart_PayloadNamingAnotherEvent_AnswersWithSilence()
    {
        var main = await IndexedMainAsync("hook-wired-main");
        var lane = Lane(main, "feat/116-wired");
        var payload = $$"""{"cwd":"{{lane}}","hook_event_name":"PreToolUse","source":"startup"}""";

        var run = await CliTestProcess.RunPipedInSharedHomeAsync(payload, lane, "hook", "session-start");

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldBeEmpty();
        RowsNaming(lane).ShouldBeEmpty();
    }

    [Fact]
    public async Task SessionStart_PayloadWithoutCwd_TakesTheLaunchDirectory()
    {
        var main = await IndexedMainAsync("hook-nocwd-main");
        var lane = Lane(main, "feat/116-nocwd");

        var run = await CliTestProcess.RunPipedInSharedHomeAsync("{}", lane, "hook", "session-start");

        run.ExitCode.ShouldBe(0);
        ContextOf(run.StdOut).ShouldContain(StoreKeyOfLane(lane, "feat/116-nocwd"));
    }

    [Fact]
    public async Task SessionStart_EmptyStdinInALane_AnswersAsItDidForTheHost()
    {
        var main = await IndexedMainAsync("hook-emptystdin-main");
        var lane = Lane(main, "feat/116-emptystdin");

        var run = await CliTestProcess.RunPipedInSharedHomeAsync(string.Empty, lane, "hook", "session-start");

        run.ExitCode.ShouldBe(0);
        ContextOf(run.StdOut).ShouldContain(StoreKeyOfLane(lane, "feat/116-emptystdin"));
    }

    [Fact]
    public async Task SessionStart_AnswerTravelsAsOneSessionStartEnvelopeWithoutAngleBrackets()
    {
        var main = await IndexedMainAsync("hook-shape-main");
        var lane = Lane(main, "feat/116-shape");

        var run = await HookAsync(lane);

        var lines = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1);
        using var document = JsonDocument.Parse(lines[0]);
        document.RootElement.TryGetProperty("hookSpecificOutput", out var hook).ShouldBeTrue();
        hook.GetProperty("hookEventName").GetString().ShouldBe("SessionStart");
        // Qwen writes this text into the system prompt after replacing '<' and '>'; the answer must not need them
        var context = hook.GetProperty("additionalContext").GetString()!;
        context.ShouldNotContain("<");
        context.ShouldNotContain(">");
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> HookAsync(string workingDir) =>
        CliTestProcess.RunPipedInSharedHomeAsync(
            $$"""{"session_id":"cli-test","transcript_path":"/dev/null","cwd":"{{workingDir}}","hook_event_name":"SessionStart","source":"startup"}""",
            workingDir,
            "hook",
            "session-start");

    private static string ContextOf(string stdOut)
    {
        stdOut.ShouldNotBeEmpty("the hook answered nothing at all");
        using var document = JsonDocument.Parse(stdOut.Trim());
        return document.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
    }

    // A parent the verb can be filled from, indexed by the product with the semantic arm off in its own config;
    // the copied lane store inherits a dead endpoint from the pre-mint below, so nothing reaches a service.
    private static async Task<string> IndexedMainAsync(string name)
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(
            Path.Combine(main, "Greeter.cs"),
            "public sealed class Greeter\n{\n    public int Count => 2;\n}\n");
        TestGitRepo.Add(main, "Greeter.cs");
        TestGitRepo.Commit(main, "seed");
        await RunCheckedAsync(main, "init", "--name", name, "--json");
        await RunCheckedAsync(main, "add", ".", "--name", "main", "--json");
        await RunCheckedAsync(main, "index", "--no-semantic", "--json");
        var config = ConfigFile.Load(main);
        ConfigFile.Save(main, config with { Indexing = config.Indexing with { NoSemantic = true } });
        return main;
    }

    private static async Task RunCheckedAsync(string workingDir, params string[] args)
    {
        var run = await CliTestProcess.RunAsync(workingDir, args);
        run.ExitCode.ShouldBe(0, $"{string.Join(' ', args)} failed: {run.StdErr}");
    }

    private static string Lane(string main, string branch)
    {
        var lane = Path.Combine(main, ".lanes", branch.Split('/')[^1]);
        TestGitRepo.AddWorktree(main, lane, branch);
        PreMintLaneStore(lane, branch);
        return lane;
    }

    private static void PreMintLaneStore(string lane, string branch)
    {
        var storeDir = Directory.CreateDirectory(
            WorkspaceStorePaths.RequireStoreDir(StoreKeyOfLane(lane, branch))).FullName;
        var childRoot = WorkspaceCanonicalPath.Canonicalize(WorkspaceCanonicalPath.ResolveThroughLinks(lane));
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(
                new WorkspaceManifest { Name = branch.Split('/')[^1], RootPath = childRoot },
                SqueezrJson.FileOptions));
        var defaults = SqueezrConfig.CreateDefault();
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.ConfigFileName),
            JsonSerializer.Serialize(
                defaults with
                {
                    Embeddings = new EmbeddingsConfig
                    {
                        Provider = "ollama",
                        Ollama = new OllamaOptions
                        {
                            Endpoint = TestWorkspaceConfig.DeadOllamaEndpoint,
                            ModelProfile = null,
                        },
                    },
                    Qdrant = new QdrantConfig { Endpoint = TestWorkspaceConfig.DeadQdrantEndpoint },
                    Indexing = defaults.Indexing with { NoSemantic = true },
                },
                SqueezrJson.FileOptions));
    }

    private static string StoreKeyOfLane(string lane, string branch)
    {
        var childRoot = WorkspaceCanonicalPath.Canonicalize(WorkspaceCanonicalPath.ResolveThroughLinks(lane));
        return WorkspaceStoreKeys.Create(branch.Split('/')[^1], childRoot);
    }

    private static string[] StoreDirsUnderStoreKey(string storeKey) =>
        Directory.Exists(SqueezrHome.WorkspacesDir)
            ? Directory.GetDirectories(SqueezrHome.WorkspacesDir, $"{storeKey}*")
            : [];

    private static JsonElement[] RowsNaming(string path)
    {
        if (!File.Exists(WorkspaceRegistry.FilePath))
        {
            return [];
        }

        var spellings = new HashSet<string>(StringComparer.Ordinal)
        {
            WorkspaceCanonicalPath.Canonicalize(path),
            WorkspaceCanonicalPath.ResolveThroughLinks(path),
        };
        using var document = JsonDocument.Parse(File.ReadAllText(WorkspaceRegistry.FilePath));
        return document.RootElement.EnumerateArray()
            .Where(row => spellings.Contains(row.GetProperty("rootPath").GetString()!))
            .Select(row => row.Clone())
            .ToArray();
    }
}
