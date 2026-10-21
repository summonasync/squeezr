using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class WorkspaceListOrphanTests
{
    [Fact]
    public async Task Json_ListsOrphansWithClassAndFlagsHusks_KeepsExistingFields()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        var adoptableRoot = TestDirs.NewTempDir();
        var legacyRoot = TestDirs.NewTempDir();
        try
        {
            var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
            var normalKey = WorkspaceStoreKeys.Create("normal", canonicalRoot);
            var registry = new List<WorkspaceRegistryEntry>
            {
                new("normal", canonicalRoot, normalKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new("husk-home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "husk-home-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            };
            WriteRegistry(home, registry);
            var (adoptableKey, _) = CreateOrphan(home, "o-adopt", adoptableRoot, adoptableRoot);
            var (legacyKey, _) = CreateOrphan(home, "o-legacy", legacyRoot, null);
            var garbageDir = Path.Combine(home, SqueezrPaths.WorkspacesDirName, "garbage-deadbeef");
            Directory.CreateDirectory(garbageDir);
            File.WriteAllText(Path.Combine(garbageDir, SqueezrPaths.StoreManifestFileName), "not json {{{");

            var result = await RunCliAsync(home, "workspace", "list", "--json");

            result.ExitCode.ShouldBe(0);
            var lines = result.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var rows = lines.Select(line => JsonDocument.Parse(line).RootElement).ToList();
            rows.Count.ShouldBe(4);

            var normal = rows.Single(r => r.GetProperty("storeKey").GetString() == normalKey);
            normal.GetProperty("name").GetString().ShouldBe("normal");
            normal.GetProperty("rootPath").GetString().ShouldBe(canonicalRoot);
            normal.GetProperty("stale").GetBoolean().ShouldBeFalse();
            normal.TryGetProperty("husk", out _).ShouldBeFalse();

            var husk = rows.Single(r => r.GetProperty("storeKey").GetString() == "husk-home-aaaa1111");
            husk.GetProperty("husk").GetBoolean().ShouldBeTrue();
            husk.TryGetProperty("type", out _).ShouldBeFalse();

            var adoptable = rows.Single(r => r.GetProperty("storeKey").GetString() == adoptableKey);
            adoptable.GetProperty("type").GetString().ShouldBe("orphan");
            adoptable.GetProperty("class").GetString().ShouldBe("adoptable");
            adoptable.GetProperty("name").GetString().ShouldBe("o-adopt");
            adoptable.GetProperty("rootPath").GetString().ShouldBe(WorkspaceCanonicalPath.Canonicalize(adoptableRoot));

            var legacy = rows.Single(r => r.GetProperty("storeKey").GetString() == legacyKey);
            legacy.GetProperty("type").GetString().ShouldBe("orphan");
            legacy.GetProperty("class").GetString().ShouldBe("legacy");
            legacy.TryGetProperty("rootPath", out _).ShouldBeFalse();

            result.StdErr.ShouldContain("squeezr workspace repair");
            result.StdErr.ShouldContain(legacyKey);
            result.StdErr.ShouldContain("squeezr drop --store-key");
            result.StdOut.ShouldNotContain("warning");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(root, recursive: true);
            Directory.Delete(adoptableRoot, recursive: true);
            Directory.Delete(legacyRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Human_ListsOrphanRowsAndHuskStatus_WithStderrHints()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        var legacyRoot = TestDirs.NewTempDir();
        try
        {
            var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
            var registry = new List<WorkspaceRegistryEntry>
            {
                new("husk-home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "husk-home-aaaa1111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            };
            WriteRegistry(home, registry);
            CreateOrphan(home, "o-adopt", root, root);
            CreateOrphan(home, "o-legacy", legacyRoot, null);

            var result = await RunCliAsync(home, "workspace", "list");

            result.ExitCode.ShouldBe(0);
            var flattened = Regex.Replace(
                Regex.Replace(result.StdOut, @"[╭╮╰╯│├┤┬┴┼─]+", " "),
                @"\s+",
                " ");
            flattened.ShouldContain("husk");
            flattened.ShouldContain("o-adopt");
            flattened.ShouldContain("o-legacy");
            flattened.ShouldContain("unknown —");
            flattened.ShouldContain("needs --root");
            result.StdErr.ShouldContain("squeezr workspace repair");
            result.StdErr.ShouldContain("squeezr drop --store-key");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(root, recursive: true);
            Directory.Delete(legacyRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Json_DeadRootOrphan_IsClassedRootGone_AndHintAsksForARestoreNotAnAdopt()
    {
        var home = TestDirs.NewTempDir();
        var deadRoot = TestDirs.NewTempDir();
        try
        {
            var canonical = WorkspaceCanonicalPath.Canonicalize(deadRoot);
            var storeKey = WorkspaceStoreKeys.Create("o-gone", canonical);
            WriteRegistry(home, []);
            CreateOrphan(home, "o-gone", deadRoot, canonical);
            Directory.Delete(deadRoot, recursive: true);

            var result = await RunCliAsync(home, "workspace", "list", "--json");

            result.ExitCode.ShouldBe(0);
            var row = result.StdOut.Trim()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement)
                .Single(r => r.GetProperty("storeKey").GetString() == storeKey);
            row.GetProperty("type").GetString().ShouldBe("orphan");
            row.GetProperty("class").GetString().ShouldBe("root-gone");
            row.GetProperty("rootPath").GetString().ShouldBe(canonical);
            result.StdErr.ShouldContain($"'{canonical}' does not exist");
            result.StdErr.ShouldNotContain("is an orphan; run 'squeezr workspace repair' to adopt it");

            var human = await RunCliAsync(home, "workspace", "list");
            var flattened = Regex.Replace(
                Regex.Replace(human.StdOut, @"[╭╮╰╯│├┤┬┴┼─]+", " "),
                @"\s+",
                " ");
            flattened.ShouldContain("orphan — root gone");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static (string StoreKey, string StoreDir) CreateOrphan(
        string home, string name, string rootDir, string? rootPath)
    {
        var storeKey = WorkspaceStoreKeys.Create(name, WorkspaceCanonicalPath.Canonicalize(rootDir));
        var storeDir = Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(
                new WorkspaceManifest { Name = name, RootPath = rootPath },
                SqueezrJson.FileOptions));
        return (storeKey, storeDir);
    }

    private static void WriteRegistry(string home, IReadOnlyList<WorkspaceRegistryEntry> registry) =>
        File.WriteAllText(
            Path.Combine(home, SqueezrPaths.RegistryFileName),
            JsonSerializer.Serialize(registry, SqueezrJson.FileOptions));

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string squeezrHome, params string[] args) =>
        CliTestProcess.RunWithHomeAsync(squeezrHome, TestDirs.NewTempDir(), args);
}
