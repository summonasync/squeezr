using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class WorkspaceRepairCommandTests
{
    [Fact]
    public async Task NoArgs_AdoptsAdoptable_ReportsNeedsRoot_IsIdempotent()
    {
        var home = TestDirs.NewTempDir();
        var adoptableRoot = TestDirs.NewTempDir();
        var legacyRoot = TestDirs.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, SqueezrPaths.RegistryFileName), "[]");
            var adoptable = CreateOrphan(home, "fix-adopt", adoptableRoot, adoptableRoot);
            var legacy = CreateOrphan(home, "fix-legacy", legacyRoot, null);

            var first = await RunCliAsync(home, "workspace", "repair");

            first.ExitCode.ShouldBe(0);
            first.StdOut.ShouldContain("adopted");
            first.StdOut.ShouldContain(adoptable.StoreKey);
            first.StdOut.ShouldContain("needs root");
            first.StdOut.ShouldContain(legacy.StoreKey);
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(adoptable.StoreKey);

            var second = await RunCliAsync(home, "workspace", "repair");

            second.ExitCode.ShouldBe(0);
            second.StdOut.ShouldContain("0 adopted");
            second.StdOut.ShouldContain("needs root");
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(adoptable.StoreKey);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(adoptableRoot, recursive: true);
            Directory.Delete(legacyRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Json_SingleObjectWithSections_HuskHintOnStderrOnly()
    {
        var home = TestDirs.NewTempDir();
        var adoptableRoot = TestDirs.NewTempDir();
        var legacyRoot = TestDirs.NewTempDir();
        try
        {
            WriteRegistry(home,
            [
                new WorkspaceRegistryEntry(
                    "husk-home",
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "husk-home-aaaa1111",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow)
            ]);
            var adoptable = CreateOrphan(home, "fix-adopt", adoptableRoot, adoptableRoot);
            var legacy = CreateOrphan(home, "fix-legacy", legacyRoot, null);

            var result = await RunCliAsync(home, "workspace", "repair", "--json");

            result.ExitCode.ShouldBe(1);
            var lines = result.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Length.ShouldBe(1);
            using var document = JsonDocument.Parse(lines[0]);
            var body = document.RootElement;
            var adopted = body.GetProperty("adopted");
            adopted.GetArrayLength().ShouldBe(1);
            adopted[0].GetProperty("storeKey").GetString().ShouldBe(adoptable.StoreKey);
            adopted[0].GetProperty("rootPath").GetString().ShouldBe(WorkspaceCanonicalPath.Canonicalize(adoptableRoot));

            var needsRoot = body.GetProperty("needsRoot");
            needsRoot.GetArrayLength().ShouldBe(1);
            needsRoot[0].GetProperty("storeKey").GetString().ShouldBe(legacy.StoreKey);
            needsRoot[0].GetProperty("name").GetString().ShouldBe("fix-legacy");
            needsRoot[0].GetProperty("reason").GetString().ShouldBe("missing-root");
            needsRoot[0].TryGetProperty("rootPath", out _).ShouldBeFalse();

            var husks = body.GetProperty("husks");
            husks.GetArrayLength().ShouldBe(1);
            husks[0].GetProperty("storeKey").GetString().ShouldBe("husk-home-aaaa1111");

            body.GetProperty("prune").GetProperty("mode").GetString().ShouldBe("refused");
            body.TryGetProperty("failedAdoptions", out _).ShouldBeFalse();
            var counts = body.GetProperty("counts");
            counts.GetProperty("adopted").GetInt32().ShouldBe(1);
            counts.GetProperty("needsRoot").GetInt32().ShouldBe(1);
            counts.GetProperty("stalePruned").GetInt32().ShouldBe(0);
            counts.GetProperty("husksPruned").GetInt32().ShouldBe(0);

            result.StdErr.ShouldContain("looks like a husk");
            result.StdErr.ShouldContain("squeezr drop --store-key");
            result.StdErr.ShouldContain("Refusing to prune 1 husk row(s) without confirmation");
            result.StdOut.ShouldNotContain("warning");
            ReadRegistry(home).Select(row => row.StoreKey).ShouldBe(["husk-home-aaaa1111", adoptable.StoreKey]);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(adoptableRoot, recursive: true);
            Directory.Delete(legacyRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Json_DeadRootOrphan_ReportsRootGone_ParksTheStore_AndAdoptsNothing()
    {
        var home = TestDirs.NewTempDir();
        var deadRoot = TestDirs.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, SqueezrPaths.RegistryFileName), "[]");
            var canonical = WorkspaceCanonicalPath.Canonicalize(deadRoot);
            var orphan = CreateOrphan(home, "fix-gone", deadRoot, canonical);
            Directory.Delete(deadRoot, recursive: true);

            var result = await RunCliAsync(home, "workspace", "repair", "--json");

            result.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(result.StdOut.Trim());
            var body = document.RootElement;
            body.GetProperty("adopted").GetArrayLength().ShouldBe(0);
            var row = body.GetProperty("needsRoot").EnumerateArray().Single();
            row.GetProperty("storeKey").GetString().ShouldBe(orphan.StoreKey);
            row.GetProperty("name").GetString().ShouldBe("fix-gone");
            row.GetProperty("reason").GetString().ShouldBe("root-gone");
            row.GetProperty("rootPath").GetString().ShouldBe(canonical);
            body.GetProperty("counts").GetProperty("needsRoot").GetInt32().ShouldBe(1);
            result.StdErr.ShouldNotContain("Refusing to prune");
            ReadRegistry(home).ShouldBeEmpty();
            Directory.Exists(orphan.StoreDir).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Theory]
    [InlineData("--store-key", "some-key")]
    [InlineData("--root", "/tmp")]
    public async Task PartialPair_ReportsUsageError(string option, string value)
    {
        var home = TestDirs.NewTempDir();
        try
        {
            string[] extraArgs = [option, value];
            var jsonArgs = extraArgs.Concat(new[] { "--json" }).ToArray();
            var json = await RunCliAsync(home, new[] { "workspace", "repair" }.Concat(jsonArgs).ToArray());

            json.ExitCode.ShouldBe(1);
            json.StdOut.ShouldBe(string.Empty);
            using var document = JsonDocument.Parse(json.StdErr.Trim());
            var error = document.RootElement.GetProperty("error");
            error.GetProperty("code").GetString().ShouldBe("invalid_usage");
            error.GetProperty("message").GetString()!.ShouldContain("--store-key");

            var human = await RunCliAsync(home, new[] { "workspace", "repair" }.Concat(extraArgs).ToArray());
            human.ExitCode.ShouldBe(1);
            human.StdErr.ShouldContain("error:");
            human.StdErr.ShouldContain("--root");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task WithRoot_ValidRoot_AdoptsLegacyOrphan()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, SqueezrPaths.RegistryFileName), "[]");
            var orphan = CreateOrphan(home, "fix-legacy", root, null);

            var result = await RunCliAsync(
                home, "workspace", "repair", "--store-key", orphan.StoreKey, "--root", root, "--json");

            result.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(result.StdOut.Trim());
            var body = document.RootElement;
            body.GetProperty("storeKey").GetString().ShouldBe(orphan.StoreKey);
            body.GetProperty("name").GetString().ShouldBe("fix-legacy");
            body.GetProperty("rootPath").GetString().ShouldBe(WorkspaceCanonicalPath.Canonicalize(root));

            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(orphan.StoreKey);
            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(orphan.StoreDir, SqueezrPaths.StoreManifestFileName)));
            manifest.RootElement.GetProperty("rootPath").GetString()
                .ShouldBe(WorkspaceCanonicalPath.Canonicalize(root));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WithRoot_WrongRoot_ReportsErrorAndKeepsRegistryAndManifest()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        var otherRoot = TestDirs.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, SqueezrPaths.RegistryFileName), "[]");
            var orphan = CreateOrphan(home, "fix-legacy", root, null);
            var manifestPath = Path.Combine(orphan.StoreDir, SqueezrPaths.StoreManifestFileName);
            var manifestBytes = File.ReadAllBytes(manifestPath);

            var result = await RunCliAsync(
                home, "workspace", "repair", "--store-key", orphan.StoreKey, "--root", otherRoot, "--json");

            result.ExitCode.ShouldBe(1);
            result.StdOut.ShouldBe(string.Empty);
            using var document = JsonDocument.Parse(result.StdErr.Trim());
            var error = document.RootElement.GetProperty("error");
            error.GetProperty("code").GetString().ShouldBe("workspace_error");
            error.GetProperty("message").GetString()!.ShouldContain("does not match store key");

            ReadRegistry(home).ShouldBeEmpty();
            File.ReadAllBytes(manifestPath).ShouldBe(manifestBytes);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(root, recursive: true);
            Directory.Delete(otherRoot, recursive: true);
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

    private static List<WorkspaceRegistryEntry> ReadRegistry(string home) =>
        JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(Path.Combine(home, SqueezrPaths.RegistryFileName)),
            SqueezrJson.FileOptions) ?? [];

    private static void WriteRegistry(string home, IReadOnlyList<WorkspaceRegistryEntry> registry) =>
        File.WriteAllText(
            Path.Combine(home, SqueezrPaths.RegistryFileName),
            JsonSerializer.Serialize(registry, SqueezrJson.FileOptions));

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string squeezrHome, params string[] args) =>
        CliTestProcess.RunWithHomeAsync(squeezrHome, TestDirs.NewTempDir(), args);
}
