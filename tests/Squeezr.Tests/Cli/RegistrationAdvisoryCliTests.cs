using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class RegistrationAdvisoryCliTests
{
    [Fact]
    public async Task Init_NameUsedByLiveRowAtDifferentRoot_WarnsWithStoreKey_AndStillRegisters()
    {
        var home = TestDirs.NewTempDir();
        var rootA = TestDirs.NewTempDir();
        var rootB = TestDirs.NewTempDir();
        try
        {
            var keyA = WorkspaceStoreKeys.Create("demo", WorkspaceCanonicalPath.Canonicalize(rootA));
            WriteRegistry(home, Row("demo", rootA, keyA));

            var run = await CliTestProcess.RunWithHomeAsync(home, rootB, "init", "--name", "demo");

            run.ExitCode.ShouldBe(0);
            run.StdErr.ShouldContain("name 'demo' is already used by workspace 'demo'");
            run.StdErr.ShouldContain(keyA);
            run.StdErr.ShouldContain("pass --name");
            ReadRegistry(home).Select(row => row.StoreKey)
                .ShouldBe([keyA, WorkspaceStoreKeys.Create("demo", WorkspaceCanonicalPath.Canonicalize(rootB))]);
        }
        finally
        {
            Clean(home, rootA, rootB);
        }
    }

    [Fact]
    public async Task Init_Json_WarningsArriveAsCodedEnvelope_AndStdoutStaysOneDocument()
    {
        var home = TestDirs.NewTempDir();
        var rootA = TestDirs.NewTempDir();
        var rootB = TestDirs.NewTempDir();
        try
        {
            var keyA = WorkspaceStoreKeys.Create("demo", WorkspaceCanonicalPath.Canonicalize(rootA));
            WriteRegistry(home, Row("demo", rootA, keyA));

            var run = await CliTestProcess.RunWithHomeAsync(home, rootB, "init", "--name", "demo", "--json");

            run.ExitCode.ShouldBe(0);
            using var result = JsonDocument.Parse(run.StdOut.Trim());
            result.RootElement.GetProperty("workspaceName").GetString().ShouldBe("demo");

            var codes = WarningCodes(run.StdErr);
            codes.ShouldContain("name_collision");
            codes.ShouldContain("temp_path_root");
            WarningTexts(run.StdErr).ShouldContain(message => message.Contains(keyA));
        }
        finally
        {
            Clean(home, rootA, rootB);
        }
    }

    [Fact]
    public async Task Init_TempRootWarning_FiresUnderTempPath_AndNotForANormalRepoPath()
    {
        var home = TestDirs.NewTempDir();
        var tempRoot = TestDirs.NewTempDir();
        var normalRoot = TestDirs.NewNonHomeScratchDir();
        try
        {
            var underTemp = await CliTestProcess.RunWithHomeAsync(home, tempRoot, "init", "--name", "temp-one");
            var outsideTemp = await CliTestProcess.RunWithHomeAsync(home, normalRoot, "init", "--name", "normal-one");

            underTemp.ExitCode.ShouldBe(0);
            underTemp.StdErr.ShouldContain("temp path");
            underTemp.StdErr.ShouldContain("pin SQUEEZR_HOME");

            outsideTemp.ExitCode.ShouldBe(0);
            outsideTemp.StdErr.ShouldNotContain("temp path");
            outsideTemp.StdErr.ShouldNotContain("pin SQUEEZR_HOME");
        }
        finally
        {
            Clean(home, tempRoot, normalRoot);
        }
    }

    [Fact]
    public async Task Add_RepoNamedLikeLiveWorkspaceAtDifferentRoot_Warns_AndRegistrationSucceeds()
    {
        var home = TestDirs.NewTempDir();
        var workspaceRoot = TestDirs.NewTempDir();
        var otherRoot = TestDirs.NewTempDir();
        var repoDir = TestDirs.NewTempDir();
        try
        {
            (await CliTestProcess.RunWithHomeAsync(home, workspaceRoot, "init", "--name", "worker")).ExitCode.ShouldBe(0);
            var otherKey = WorkspaceStoreKeys.Create("demo", WorkspaceCanonicalPath.Canonicalize(otherRoot));
            WriteRegistry(
                home,
                Row("worker", workspaceRoot, WorkspaceStoreKeys.Create("worker", WorkspaceCanonicalPath.Canonicalize(workspaceRoot))),
                Row("demo", otherRoot, otherKey));

            var run = await CliTestProcess.RunWithHomeAsync(home, workspaceRoot, "add", repoDir, "--name", "demo");

            run.ExitCode.ShouldBe(0);
            run.StdErr.ShouldContain("name 'demo' is already used by workspace 'demo'");
            run.StdErr.ShouldContain(otherKey);
            ReadManifestRepos(home, WorkspaceStoreKeys.Create("worker", WorkspaceCanonicalPath.Canonicalize(workspaceRoot)))
                .ShouldContain(repo => repo.GetProperty("name").GetString() == "demo");
        }
        finally
        {
            Clean(home, workspaceRoot, otherRoot, repoDir);
        }
    }

    [Fact]
    public async Task Add_RepoNamedLikeTheEnclosingWorkspace_WarnsWithItsStoreKey()
    {
        var home = TestDirs.NewTempDir();
        var workspaceRoot = TestDirs.NewTempDir();
        var repoDir = TestDirs.NewTempDir();
        try
        {
            (await CliTestProcess.RunWithHomeAsync(home, workspaceRoot, "init", "--name", "worker")).ExitCode.ShouldBe(0);
            var workspaceKey = WorkspaceStoreKeys.Create("worker", WorkspaceCanonicalPath.Canonicalize(workspaceRoot));

            var run = await CliTestProcess.RunWithHomeAsync(home, workspaceRoot, "add", repoDir, "--name", "worker");

            run.ExitCode.ShouldBe(0);
            run.StdErr.ShouldContain("name 'worker' is already used by workspace 'worker'");
            run.StdErr.ShouldContain(workspaceKey);
        }
        finally
        {
            Clean(home, workspaceRoot, repoDir);
        }
    }

    [Fact]
    public async Task Add_WorkspaceRootAsRepo_NoSelfCollisionWarning()
    {
        var home = TestDirs.NewTempDir();
        var workspaceRoot = TestDirs.NewTempDir();
        try
        {
            (await CliTestProcess.RunWithHomeAsync(home, workspaceRoot, "init", "--name", "worker")).ExitCode.ShouldBe(0);

            var run = await CliTestProcess.RunWithHomeAsync(home, workspaceRoot, "add", ".", "--name", "worker");

            run.ExitCode.ShouldBe(0);
            run.StdErr.ShouldNotContain("already used by workspace");
        }
        finally
        {
            Clean(home, workspaceRoot);
        }
    }

    [Fact]
    public async Task Add_UniqueRepoName_NoNameCollisionWarning()
    {
        var home = TestDirs.NewTempDir();
        var workspaceRoot = TestDirs.NewTempDir();
        var otherRoot = TestDirs.NewTempDir();
        var repoDir = TestDirs.NewTempDir();
        try
        {
            (await CliTestProcess.RunWithHomeAsync(home, workspaceRoot, "init", "--name", "worker")).ExitCode.ShouldBe(0);
            WriteRegistry(
                home,
                Row("worker", workspaceRoot, WorkspaceStoreKeys.Create("worker", WorkspaceCanonicalPath.Canonicalize(workspaceRoot))),
                Row("demo", otherRoot, WorkspaceStoreKeys.Create("demo", WorkspaceCanonicalPath.Canonicalize(otherRoot))));

            var run = await CliTestProcess.RunWithHomeAsync(home, workspaceRoot, "add", repoDir, "--name", "solo");

            run.ExitCode.ShouldBe(0);
            run.StdErr.ShouldNotContain("already used by workspace");
        }
        finally
        {
            Clean(home, workspaceRoot, otherRoot, repoDir);
        }
    }

    private static List<string> WarningCodes(string stdErr) =>
        WarningEnvelope(stdErr).Select(warning => warning.GetProperty("code").GetString()!).ToList();

    private static List<string> WarningTexts(string stdErr) =>
        WarningEnvelope(stdErr).Select(warning => warning.GetProperty("message").GetString()!).ToList();

    private static List<JsonElement> WarningEnvelope(string stdErr)
    {
        var envelope = stdErr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Last(line => line.TrimStart().StartsWith('{'));
        using var document = JsonDocument.Parse(envelope);
        return document.RootElement
            .GetProperty("warnings")
            .EnumerateArray()
            .Select(element => element.Clone())
            .ToList();
    }

    private static IEnumerable<JsonElement> ReadManifestRepos(string home, string storeKey)
    {
        var manifestPath = Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey, SqueezrPaths.StoreManifestFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return document.RootElement
            .GetProperty("repos")
            .EnumerateArray()
            .Select(element => element.Clone())
            .ToList();
    }

    private static WorkspaceRegistryEntry Row(string name, string root, string storeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(name, WorkspaceCanonicalPath.Canonicalize(root), storeKey, now, now);
    }

    private static void WriteRegistry(string home, params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            Path.Combine(home, SqueezrPaths.RegistryFileName),
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private static List<WorkspaceRegistryEntry> ReadRegistry(string home) =>
        JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(Path.Combine(home, SqueezrPaths.RegistryFileName)),
            SqueezrJson.FileOptions) ?? [];

    private static void Clean(string home, params string[] extraDirs)
    {
        foreach (var dir in new[] { home }.Concat(extraDirs))
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
