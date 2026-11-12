using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class WorktreeInitCliTests
{
    [Fact]
    public async Task WorktreeInit_LinkedChildOfIndexedMain_RegistersItsOwnStoreAndStatusNamesTheChild()
    {
        var main = SeededRepo();
        (await Run(main, "init", "--name", "cli-main", "--json")).ExitCode.ShouldBe(0);
        var child = AddWorktree(main, "feat/116-cli-reg");

        var run = await Run(child, "worktree", "init", "--json");

        run.ExitCode.ShouldBe(0);
        using var report = JsonDocument.Parse(run.StdOut.Trim());
        var payload = report.RootElement;
        payload.GetProperty("state").GetString().ShouldBe("Registered");
        var storeKey = payload.GetProperty("storeKey").GetString()!;
        storeKey.ShouldStartWith("116-cli-reg-");
        payload.GetProperty("rootPath").GetString()
            .ShouldBe(WorkspaceCanonicalPath.Canonicalize(child));
        payload.GetProperty("parentRootPath").GetString()
            .ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        payload.GetProperty("kind").GetString().ShouldBe("Worktree");
        payload.GetProperty("branch").GetString().ShouldBe("feat/116-cli-reg");
        payload.GetProperty("isLinked").GetBoolean().ShouldBeTrue();
        payload.GetProperty("reconcilePending").GetBoolean().ShouldBeTrue();
        payload.GetProperty("collection").GetString()!.ShouldBe(CollectionOf(child));
        payload.GetProperty("collection").GetString()!
            .ShouldNotBe(CollectionOf(main), "a lane must not inherit the main worktree's collection");
        payload.GetProperty("serveCommand").GetString()
            .ShouldBe($"cd '{WorkspaceCanonicalPath.Canonicalize(child)}' && squeezr serve");

        var row = RegistryRowOf(child);
        row.GetProperty("rootPath").GetString().ShouldBe(WorkspaceCanonicalPath.Canonicalize(child));
        row.GetProperty("parentRootPath").GetString().ShouldBe(WorkspaceCanonicalPath.Canonicalize(main));
        row.GetProperty("kind").GetString().ShouldBe("Worktree");
        row.GetProperty("branch").GetString().ShouldBe("feat/116-cli-reg");
        File.Exists(Path.Combine(StoreDir(storeKey), SqueezrPaths.MetaDbFileName)).ShouldBeTrue();

        var status = await Run(child, "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var statusDoc = JsonDocument.Parse(status.StdOut.Trim());
        statusDoc.RootElement.GetProperty("name").GetString().ShouldBe("116-cli-reg");
        var current = statusDoc.RootElement.GetProperty("workspaces").EnumerateArray()
            .Single(entry => entry.GetProperty("current").GetBoolean());
        current.GetProperty("rootPath").GetString().ShouldBe(WorkspaceCanonicalPath.Canonicalize(child));
        current.GetProperty("storeKey").GetString().ShouldBe(storeKey);
    }

    [LinuxFact]
    public async Task WorktreeInit_MintedThroughAnAncestorLink_IsResolvableFromThePhysicalLaneCwd()
    {
        var main = SeededRepo();
        (await Run(main, "init", "--name", "cli-main", "--json")).ExitCode.ShouldBe(0);
        var child = AddWorktree(main, "feat/116-cli-link");
        var link = Path.Combine(TestDirs.NewTempDir(), "link-to-main");
        Directory.CreateSymbolicLink(link, main);

        var run = await Run(link, "worktree", "init", "--json", Path.Combine(link, ".lanes", "116-cli-link"));

        run.ExitCode.ShouldBe(0);
        string storeKey;
        using (var report = JsonDocument.Parse(run.StdOut.Trim()))
        {
            var payload = report.RootElement;
            payload.GetProperty("state").GetString().ShouldBe("Registered");
            storeKey = payload.GetProperty("storeKey").GetString()!;
            payload.GetProperty("rootPath").GetString()
                .ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(child),
                    "the row holds the physical root, not the spelling the caller arrived under");
        }

        var status = await Run(child, "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var statusDoc = JsonDocument.Parse(status.StdOut.Trim());
        statusDoc.RootElement.GetProperty("name").GetString().ShouldBe("116-cli-link",
            "discovery from the lane's own cwd must reach the lane's store, not the parent's");
        var current = statusDoc.RootElement.GetProperty("workspaces").EnumerateArray()
            .Single(entry => entry.GetProperty("current").GetBoolean());
        current.GetProperty("rootPath").GetString().ShouldBe(WorkspaceCanonicalPath.ResolveThroughLinks(child));
        current.GetProperty("storeKey").GetString().ShouldBe(storeKey);
    }

    [Fact]
    public async Task WorktreeInit_RunTwiceThenTheBareVerb_KeepsOneRowOneStoreAndReportsTheSameKey()
    {
        var main = SeededRepo();
        (await Run(main, "init", "--name", "cli-main", "--json")).ExitCode.ShouldBe(0);
        var child = AddWorktree(main, "feat/116-cli-idem");
        var first = await Run(child, "worktree", "init", "--json");
        first.ExitCode.ShouldBe(0);
        var storeKey = JsonDocument.Parse(first.StdOut.Trim()).RootElement.GetProperty("storeKey").GetString()!;

        var second = await Run(child, "worktree", "init", "--json");
        var bare = await Run(child, "worktree", "--json");

        foreach (var run in new[] { second, bare })
        {
            run.ExitCode.ShouldBe(0);
            using var doc = JsonDocument.Parse(run.StdOut.Trim());
            doc.RootElement.GetProperty("state").GetString().ShouldBe("AlreadyRegistered");
            doc.RootElement.GetProperty("storeKey").GetString().ShouldBe(storeKey);
            doc.RootElement.TryGetProperty("reconcilePending", out _).ShouldBeFalse(
                "an adopted store has no reconcile evidence yet — the field is absent, never a claimed false");
        }

        RegistryRowsUnder(child).Length.ShouldBe(1);
        StoreDirKeysStartingWith("116-cli-idem-").Length.ShouldBe(1);

        var human = await Run(child, "worktree");
        human.ExitCode.ShouldBe(0);
        human.StdOut.ShouldContain("nothing created");
        human.StdOut.ShouldContain("index: reconcile state unknown until the store reports it");
    }

    [Fact]
    public async Task WorktreeInit_NameAskingForAnotherStore_RefusesWithADropThatExecutes()
    {
        var main = SeededRepo();
        (await Run(main, "init", "--name", "cli-main", "--json")).ExitCode.ShouldBe(0);
        var child = AddWorktree(main, "feat/116-cli-clash");
        var registered = await Run(child, "worktree", "init", "--json");
        registered.ExitCode.ShouldBe(0);
        var storeKey = JsonDocument.Parse(registered.StdOut.Trim()).RootElement.GetProperty("storeKey").GetString()!;
        var registeredRoot = JsonDocument.Parse(registered.StdOut.Trim())
            .RootElement.GetProperty("rootPath").GetString()!;

        var clash = await Run(child, "worktree", "init", "--name", "another-lane", "--json");

        clash.ExitCode.ShouldBe(1);
        clash.StdOut.ShouldBeEmpty();
        var error = ParseStdErrError(clash.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.WorktreeNameClashCode);
        var message = error.GetProperty("message").GetString()!;
        message.ShouldContain(storeKey);
        message.ShouldContain($"squeezr worktree drop '{registeredRoot}'", Case.Sensitive,
            "the remedy names the verb that clears every row of this one tree, not one row at a time");
        RegistryRowsUnder(child).Length.ShouldBe(1);
        StoreDirKeysStartingWith("another-lane-").ShouldBeEmpty();
    }

    [Fact]
    public async Task WorktreeInit_SeparateGitDirNamesNoParentRootAndCreatesNothingInBothModes()
    {
        var baseDir = TestDirs.NewTempDir();
        var main = Path.Combine(baseDir, "main");
        Directory.CreateDirectory(main);
        TestGitRepo.Init(main, Path.Combine(baseDir, "gitdir"));
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        var child = Path.Combine(baseDir, "lane-sep");
        TestGitRepo.AddWorktree(main, child, "feat/116-cli-sep");

        var run = await Run(child, "worktree", "init", "--json");

        run.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        doc.RootElement.GetProperty("state").GetString().ShouldBe("NoParentRootToName",
            "a parent git cannot name is a different fact from a parent that is not indexed");
        doc.RootElement.GetProperty("reason").GetString()!.ShouldContain("not spelled '<main root>/.git'");
        doc.RootElement.TryGetProperty("storeKey", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("reconcilePending", out _).ShouldBeFalse();
        RegistryRowsUnder(child).ShouldBeEmpty();
        StoreDirKeysStartingWith("116-cli-sep-").ShouldBeEmpty();

        var human = await Run(child, "worktree", "init");

        human.ExitCode.ShouldBe(0);
        human.StdOut.ShouldContain("Cannot find the main worktree root");
    }

    [Fact]
    public async Task WorktreeInit_MainWorktreeNotIndexed_MintsNothingAndExitsZero()
    {
        var main = SeededRepo();
        var child = AddWorktree(main, "feat/116-cli-noparent");

        var run = await Run(child, "worktree", "init", "--json");

        run.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(run.StdOut.Trim());
        doc.RootElement.GetProperty("state").GetString().ShouldBe("ParentNotIndexed");
        doc.RootElement.GetProperty("reason").GetString()!.ShouldContain("is not an indexed workspace");
        doc.RootElement.GetProperty("rootPath").GetString()
            .ShouldBe(WorkspaceCanonicalPath.Canonicalize(child), "one rootPath shape across every path");
        doc.RootElement.TryGetProperty("storeKey", out _).ShouldBeFalse(
            "a pass that created no store names no key — the CLI output contract drops null fields");
        doc.RootElement.TryGetProperty("reconcilePending", out _).ShouldBeFalse(
            "there is no store here to call un-reconciled or reconciled");
        RegistryRowsUnder(child).ShouldBeEmpty();
        StoreDirKeysStartingWith("116-cli-noparent-").ShouldBeEmpty();
        Directory.Exists(Path.Combine(child, SqueezrPaths.StoreDirName)).ShouldBeFalse();
        File.Exists(Path.Combine(child, SqueezrPaths.ManifestFileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task WorktreeInit_PathWithNoGitMarker_RefusesWithNotAWorktreeCode()
    {
        var outside = TestDirs.NewTempDir();

        var run = await Run(outside, "worktree", "init", "--json");

        run.ExitCode.ShouldBe(1);
        run.StdOut.ShouldBeEmpty();
        var error = ParseStdErrError(run.StdErr);
        error.GetProperty("code").GetString().ShouldBe(WorkspaceException.NotAWorktreeCode);
        error.GetProperty("message").GetString()!.ShouldContain("squeezr init");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> Run(
        string workingDir, params string[] args) => await CliTestProcess.RunAsync(workingDir, args);

    private static string SeededRepo()
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        return main;
    }

    private static string AddWorktree(string main, string branch)
    {
        var leaf = branch.Split('/')[^1];
        var child = Path.Combine(main, ".lanes", leaf);
        TestGitRepo.AddWorktree(main, child, branch);
        return child;
    }

    // The report must name the collection this store will open, not the parent's — one collection per
    // root is what keeps a lane's vectors out of the main worktree's index.
    private static string CollectionOf(string childRoot) =>
        QdrantVectorStore.GetCollectionName(WorkspaceCanonicalPath.Canonicalize(childRoot));

    private static JsonElement RegistryRowOf(string path) => RegistryRowsUnder(path).Single();

    private static JsonElement[] RegistryRowsUnder(string path)
    {
        var registry = WorkspaceRegistry.FilePath;
        if (!File.Exists(registry))
        {
            return [];
        }

        var canonical = WorkspaceCanonicalPath.Canonicalize(path);
        using var doc = JsonDocument.Parse(File.ReadAllText(registry));
        return doc.RootElement.EnumerateArray()
            .Where(row => row.GetProperty("rootPath").GetString() == canonical)
            .Select(row => row.Clone())
            .ToArray();
    }

    private static string[] StoreDirKeysStartingWith(string prefix) =>
        Directory.Exists(SqueezrHome.WorkspacesDir)
            ? Directory.GetDirectories(SqueezrHome.WorkspacesDir)
                .Select(dir => new DirectoryInfo(dir).Name)
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray()
            : [];

    private static string StoreDir(string storeKey) =>
        Path.Combine(SqueezrHome.WorkspacesDir, storeKey);

    private static JsonElement ParseStdErrError(string stdErr)
    {
        var lines = stdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var doc = JsonDocument.Parse(lines[^1]);
        return doc.RootElement.GetProperty("error").Clone();
    }
}
