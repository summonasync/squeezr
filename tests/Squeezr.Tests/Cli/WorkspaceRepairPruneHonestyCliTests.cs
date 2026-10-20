using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class WorkspaceRepairPruneHonestyCliTests
{
    private static readonly UnixFileMode HomeWritable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Yes_ReadOnlyHome_Json_CountsTheStoreDirThatDisappeared()
    {
        var home = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var goneKey = Key("prune-gone", goneRoot);
        var goneDir = StoreDir(home, goneKey);
        try
        {
            WriteRegistry(home, Row("prune-gone", goneRoot, goneKey));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot);
            Directory.Delete(goneRoot, recursive: true);
            File.SetUnixFileMode(home, HomeWritable & ~UnixFileMode.UserWrite);

            var run = await RunAsync(home, "workspace", "repair", "--yes", "--json");

            run.ExitCode.ShouldBe(1);
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            var prune = document.RootElement.GetProperty("prune");
            prune.GetProperty("mode").GetString().ShouldBe("aborted");
            prune.GetProperty("storesRemoved").GetInt32().ShouldBe(1);
            var row = prune.GetProperty("rows").EnumerateArray().Single();
            row.GetProperty("action").GetString().ShouldBe("store-removed-row-kept");
            row.GetProperty("removedPaths").EnumerateArray().Select(path => path.GetString())
                .ShouldBe([goneDir]);
            row.GetProperty("failure").GetString()!.ShouldContain("the store dir was removed");
            row.GetProperty("failure").GetString()!.ShouldNotContain("a store dir could not be removed");
            using var error = JsonDocument.Parse(Envelope(run.StdErr));
            error.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("workspace_error");
            Directory.Exists(goneDir).ShouldBeFalse();
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(goneKey);
        }
        finally
        {
            if (Directory.Exists(home))
            {
                File.SetUnixFileMode(home, HomeWritable);
            }

            Clean(home, goneRoot);
        }
    }

    // A row can only carry NUL through a hand-edited workspaces.json: argv cannot express it. The sweep
    // has to refuse the form and delete nothing, not reach the path resolver with it.
    [Fact]
    public async Task Yes_StoreKeyWithNulByte_IsLeftAloneAndNothingDisappears()
    {
        var home = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var nulKey = "a\0b";
        var workspaces = Path.Combine(home, SqueezrPaths.WorkspacesDirName);
        try
        {
            WriteRegistry(home, Row("prune-nul", goneRoot, nulKey));
            Directory.CreateDirectory(workspaces);
            Directory.Delete(goneRoot, recursive: true);

            var human = await RunAsync(home, "workspace", "repair", "--yes");
            var json = await RunAsync(home, "workspace", "repair", "--yes", "--json");

            human.ExitCode.ShouldBe(0);
            human.StdOut.ShouldContain("left alone, store key is not a single path segment: prune-nul");
            human.StdOut.ShouldContain("1 left alone");
            human.StdOut.ShouldContain("no store dirs removed");
            json.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(json.StdOut.Trim());
            document.RootElement.GetProperty("prune").GetProperty("storesRemoved").GetInt32().ShouldBe(0);
            var row = document.RootElement.GetProperty("prune").GetProperty("rows").EnumerateArray().Single();
            row.GetProperty("storeKey").GetString().ShouldBe(nulKey);
            row.GetProperty("action").GetString().ShouldBe("skipped-unsafe-store-key");
            row.GetProperty("removedPaths").GetArrayLength().ShouldBe(0);
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(nulKey);
            Directory.GetFileSystemEntries(workspaces).ShouldBeEmpty();
        }
        finally
        {
            Clean(home, goneRoot);
        }
    }

    private static string Envelope(string stdErr) =>
        stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Last(line => line.TrimStart().StartsWith('{'));
    [Fact]
    public async Task Yes_GuardSkippedRowWithNestedStoreDir_SaysNoStoreDirsRemoved()
    {
        var home = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var evilRoot = Canonical(TestDirs.NewTempDir());
        var goneKey = Key("prune-gone", goneRoot);
        var nestedDir = Path.Combine(home, SqueezrPaths.WorkspacesDirName, "a", "b");
        try
        {
            WriteRegistry(
                home,
                Row("prune-gone", goneRoot, goneKey),
                Row("prune-evil", evilRoot, "a/b"));
            Directory.CreateDirectory(nestedDir);
            Directory.Delete(goneRoot, recursive: true);
            Directory.Delete(evilRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair", "--yes");

            run.ExitCode.ShouldBe(0);
            run.StdOut.ShouldContain("no store dirs removed");
            run.StdOut.ShouldNotContain("no store dirs existed");
            run.StdOut.ShouldContain("1 left alone");
            Directory.Exists(nestedDir).ShouldBeTrue();
            ReadRegistry(home).Select(row => row.StoreKey).ShouldBe(["a/b"]);
        }
        finally
        {
            Clean(home, goneRoot, evilRoot);
        }
    }

    [Fact]
    public async Task DryRun_RowsWithoutStoreDirs_PromisesNoStoreDirsToRemove()
    {
        var home = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        try
        {
            WriteRegistry(home, Row("prune-gone", goneRoot, Key("prune-gone", goneRoot)));
            Directory.Delete(goneRoot, recursive: true);

            var dry = await RunAsync(home, "workspace", "repair", "--dry-run");

            dry.ExitCode.ShouldBe(0);
            dry.StdOut.ShouldContain("registry rows only — no store dirs to remove");
            dry.StdOut.ShouldNotContain("no store dirs exist.");
        }
        finally
        {
            Clean(home, goneRoot);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Yes_StoreDirUndeletable_FailureNamesTheStoreDirClass()
    {
        var home = TestDirs.NewTempDir();
        var blockedRoot = Canonical(TestDirs.NewTempDir());
        var blockedKey = Key("prune-zulu", blockedRoot);
        var blockedDir = StoreDir(home, blockedKey);
        try
        {
            WriteRegistry(home, Row("prune-zulu", blockedRoot, blockedKey));
            WriteStoreDir(home, blockedKey, "prune-zulu", blockedRoot);
            Directory.Delete(blockedRoot, recursive: true);
            File.SetUnixFileMode(blockedDir, UnixFileMode.None);

            var run = await RunAsync(home, "workspace", "repair", "--yes");

            run.ExitCode.ShouldBe(1);
            run.StdOut.ShouldContain(": a store dir could not be removed");
            run.StdOut.ShouldNotContain("the registry row could not be pruned");
            run.StdOut.ShouldContain("0 store dir(s) gone");
            run.StdOut.ShouldNotContain("1 store dir(s) gone");
            run.StdOut.ShouldNotContain("removed: unverified");
            run.StdErr.ShouldNotContain("registry is busy");
            Directory.Exists(blockedDir).ShouldBeTrue();
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(blockedKey);
        }
        finally
        {
            Unlock(home);
            Clean(home, blockedRoot);
        }
    }

    [Fact]
    public async Task Yes_RegistryWriteBlocked_FailureNamesTheRegistryRowClass()
    {
        var home = TestDirs.NewTempDir();
        var goneRoot = Canonical(TestDirs.NewTempDir());
        var goneKey = Key("prune-gone", goneRoot);
        var goneDir = StoreDir(home, goneKey);
        using var registryLock = new FileStream(
            Path.Combine(home, SqueezrPaths.RegistryLockFileName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        try
        {
            WriteRegistry(home, Row("prune-gone", goneRoot, goneKey));
            WriteStoreDir(home, goneKey, "prune-gone", goneRoot);
            Directory.Delete(goneRoot, recursive: true);

            var run = await RunAsync(home, "workspace", "repair", "--yes");

            run.ExitCode.ShouldBe(1);
            run.StdOut.ShouldContain("the registry row could not be pruned");
            run.StdOut.ShouldNotContain("a store dir could not be removed");
            run.StdOut.ShouldContain("1 store dir(s) gone");
            run.StdOut.ShouldNotContain("0 store dir(s) gone");
            run.StdOut.ShouldContain("removed: unverified");
            Directory.Exists(goneDir).ShouldBeFalse();
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(goneKey);
        }
        finally
        {
            Clean(home, goneRoot);
        }
    }

    private static List<WorkspaceRegistryEntry> ReadRegistry(string home) =>
        JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(RegistryPath(home)), SqueezrJson.FileOptions) ?? [];

    private static void WriteRegistry(string home, params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(RegistryPath(home), JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private static void WriteStoreDir(string home, string storeKey, string name, string? rootPath)
    {
        var storeDir = StoreDir(home, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(new WorkspaceManifest { Name = name, RootPath = rootPath }, SqueezrJson.FileOptions));
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName), new string('x', 256));
    }

    [SupportedOSPlatform("linux")]
    private static void Unlock(string home)
    {
        var workspaces = Path.Combine(home, SqueezrPaths.WorkspacesDirName);
        foreach (var dir in Directory.EnumerateDirectories(workspaces))
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static WorkspaceRegistryEntry Row(string name, string canonicalRoot, string storeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(name, canonicalRoot, storeKey, now, now);
    }

    private static string Key(string name, string canonicalRoot) => WorkspaceStoreKeys.Create(name, canonicalRoot);

    private static string StoreDir(string home, string storeKey) =>
        Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey);

    private static string RegistryPath(string home) => Path.Combine(home, SqueezrPaths.RegistryFileName);

    private static string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string home, params string[] args) =>
        CliTestProcess.RunWithHomeAsync(home, TestDirs.NewTempDir(), args);

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
