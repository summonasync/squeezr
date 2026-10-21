using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class DropStoreKeyGuardCliTests
{
    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".")]
    public async Task Drop_TraversalStoreKey_RefusesBeforeReadingTheRegistry(string storeKey)
    {
        var home = TestDirs.NewTempDir();
        var victimDir = StoreDir(home, "victim-key");
        try
        {
            WriteRegistry(home, Row("prune-evil", Canonical(TestDirs.NewTempDir()), storeKey));
            WriteStoreDir(home, "victim-key", "victim");
            File.WriteAllText(Path.Combine(home, "canary.txt"), "keep me");

            var drop = await RunAsync(home, "drop", "--store-key", storeKey);

            drop.ExitCode.ShouldBe(1);
            drop.StdErr.ShouldContain("not a single path segment");
            drop.StdErr.ShouldNotContain("registry is busy");
            drop.StdErr.ShouldNotContain("at Squeezr");
            Directory.Exists(home).ShouldBeTrue();
            Directory.Exists(victimDir).ShouldBeTrue();
            File.ReadAllText(Path.Combine(home, "canary.txt")).ShouldBe("keep me");
            ReadRegistry(home).Select(row => row.StoreKey).ShouldBe([storeKey]);
        }
        finally
        {
            Clean(home);
        }
    }

    [Fact]
    public async Task Drop_ColonStoreKey_RefusesTheSameWayOnPosixAsOnWindows()
    {
        var home = TestDirs.NewTempDir();
        try
        {
            WriteRegistry(home, Row("stream", Canonical(TestDirs.NewTempDir()), "..:stream"));

            var drop = await RunAsync(home, "drop", "--store-key", "..:stream");

            drop.ExitCode.ShouldBe(1);
            drop.StdErr.ShouldContain("not a single path segment");
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe("..:stream");
        }
        finally
        {
            Clean(home);
        }
    }

    [Fact]
    public async Task Drop_TraversalStoreKey_Json_ReportsWorkspaceErrorEnvelope()
    {
        var home = TestDirs.NewTempDir();
        try
        {
            WriteRegistry(home, Row("prune-evil", Canonical(TestDirs.NewTempDir()), ".."));

            var drop = await RunAsync(home, "drop", "--store-key", "..", "--json");

            drop.ExitCode.ShouldBe(1);
            drop.StdOut.ShouldBeEmpty();
            using var error = JsonDocument.Parse(drop.StdErr.Trim());
            var envelope = error.RootElement.GetProperty("error");
            envelope.GetProperty("code").GetString().ShouldBe(WorkspaceException.DefaultCode);
            envelope.GetProperty("message").GetString()!.ShouldContain("not a single path segment");
            Directory.Exists(home).ShouldBeTrue();
        }
        finally
        {
            Clean(home);
        }
    }

    [Theory]
    [InlineData("проект-проект")]
    [InlineData("café-work")]
    [InlineData("דגם")]
    public async Task Drop_NonAsciiStoreKey_RemovesRowAndStore(string name)
    {
        var home = TestDirs.NewTempDir();
        var root = Canonical(TestDirs.NewTempDir());
        var storeKey = Key(name, root);
        var storeDir = StoreDir(home, storeKey);
        try
        {
            WriteRegistry(home, Row(name, root, storeKey));
            WriteStoreDir(home, storeKey, name);
            Directory.Delete(root, recursive: true);

            var drop = await RunAsync(home, "drop", "--store-key", storeKey, "--json");

            drop.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(drop.StdOut.Trim());
            document.RootElement.GetProperty("removed")[0].GetString().ShouldBe(storeDir);
            ReadRegistry(home).ShouldBeEmpty();
            Directory.Exists(storeDir).ShouldBeFalse();
        }
        finally
        {
            Clean(home, root);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Drop_StoreDirUndeletable_ReportsRemovalFailure_NotRegistryBusy()
    {
        var home = TestDirs.NewTempDir();
        const string storeKey = "blocked-1a2b3c4d";
        var storeDir = StoreDir(home, storeKey);
        try
        {
            WriteRegistry(home, Row("blocked", Canonical(TestDirs.NewTempDir()), storeKey));
            WriteStoreDir(home, storeKey, "blocked");
            File.SetUnixFileMode(storeDir, UnixFileMode.None);

            var drop = await RunAsync(home, "drop", "--store-key", storeKey, "--json");

            drop.ExitCode.ShouldBe(1);
            drop.StdErr.ShouldContain("Could not remove the store dir");
            drop.StdErr.ShouldNotContain("registry is busy");
            using var error = JsonDocument.Parse(Envelope(drop.StdErr));
            error.RootElement.GetProperty("error").GetProperty("code").GetString()
                .ShouldBe(WorkspaceException.DefaultCode);
            Directory.Exists(storeDir).ShouldBeTrue();
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        }
        finally
        {
            UnlockStoreDirs(home);
            Clean(home);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void UnlockStoreDirs(string home)
    {
        var workspaces = Path.Combine(home, SqueezrPaths.WorkspacesDirName);
        foreach (var dir in Directory.EnumerateDirectories(workspaces))
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string Envelope(string stdErr) =>
        stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Last(line => line.TrimStart().StartsWith('{'));

    private static List<WorkspaceRegistryEntry> ReadRegistry(string home) =>
        JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(Path.Combine(home, SqueezrPaths.RegistryFileName)), SqueezrJson.FileOptions) ?? [];

    private static void WriteRegistry(string home, params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            Path.Combine(home, SqueezrPaths.RegistryFileName),
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private static void WriteStoreDir(string home, string storeKey, string name)
    {
        var storeDir = StoreDir(home, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(new WorkspaceManifest { Name = name }, SqueezrJson.FileOptions));
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName), new string('x', 64));
    }

    private static WorkspaceRegistryEntry Row(string name, string canonicalRoot, string storeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(name, canonicalRoot, storeKey, now, now);
    }

    private static string Key(string name, string canonicalRoot) => WorkspaceStoreKeys.Create(name, canonicalRoot);

    private static string StoreDir(string home, string storeKey) =>
        Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey);

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
