using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class NameCollisionMarkingCliTests
{
    private const string CollisionName = "demo";

    [Fact]
    public async Task List_TwoStaleDemoRows_MarksBoth_AndWarnsWithBothStoreKeys()
    {
        var home = TestDirs.NewTempDir();
        var rootA = TestDirs.NewTempDir();
        var rootB = TestDirs.NewTempDir();
        try
        {
            var keyA = WorkspaceStoreKeys.Create(CollisionName, WorkspaceCanonicalPath.Canonicalize(rootA));
            var keyB = WorkspaceStoreKeys.Create(CollisionName, WorkspaceCanonicalPath.Canonicalize(rootB));
            WriteRegistry(home, Row(CollisionName, rootA, keyA), Row(CollisionName, rootB, keyB));
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);

            var run = await RunAsync(home, "workspace", "list");

            run.ExitCode.ShouldBe(0);
            Regex.Matches(run.StdOut, @"name shared").Count.ShouldBe(2);
            run.StdOut.ShouldContain(keyA);
            run.StdOut.ShouldContain(keyB);
            run.StdErr.ShouldContain($"run 'squeezr drop --store-key {keyA}'");
            run.StdErr.ShouldContain($"run 'squeezr drop --store-key {keyB}'");
        }
        finally
        {
            Clean(home, rootA, rootB);
        }
    }

    [Fact]
    public async Task Fleet_TwoStaleDemoRows_MarksBoth_AndWarnsWithBothStoreKeys()
    {
        var home = TestDirs.NewTempDir();
        var rootA = TestDirs.NewTempDir();
        var rootB = TestDirs.NewTempDir();
        try
        {
            var keyA = WorkspaceStoreKeys.Create(CollisionName, WorkspaceCanonicalPath.Canonicalize(rootA));
            var keyB = WorkspaceStoreKeys.Create(CollisionName, WorkspaceCanonicalPath.Canonicalize(rootB));
            WriteRegistry(home, Row(CollisionName, rootA, keyA), Row(CollisionName, rootB, keyB));
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);

            var run = await RunAsync(home, "fleet");

            run.ExitCode.ShouldBe(0);
            Regex.Matches(run.StdOut, @"name shared").Count.ShouldBe(2);
            run.StdErr.ShouldContain($"run 'squeezr drop --store-key {keyA}'");
            run.StdErr.ShouldContain($"run 'squeezr drop --store-key {keyB}'");
        }
        finally
        {
            Clean(home, rootA, rootB);
        }
    }

    [Fact]
    public async Task List_Json_NameCollisionField_LandsOnlyOnCollidingRows()
    {
        var home = TestDirs.NewTempDir();
        var rootA = TestDirs.NewTempDir();
        var rootB = TestDirs.NewTempDir();
        try
        {
            var uniqueKey = WorkspaceStoreKeys.Create("unique", WorkspaceCanonicalPath.Canonicalize(rootB));
            WriteRegistry(
                home,
                Row(CollisionName, rootA, WorkspaceStoreKeys.Create(CollisionName, WorkspaceCanonicalPath.Canonicalize(rootA))),
                Row(CollisionName, rootB, WorkspaceStoreKeys.Create(CollisionName, WorkspaceCanonicalPath.Canonicalize(rootB))),
                Row("unique", rootB, uniqueKey));

            var run = await RunAsync(home, "workspace", "list", "--json");

            run.ExitCode.ShouldBe(0);
            var rows = run.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement)
                .ToList();
            rows.Count.ShouldBe(3);
            var colliding = rows.Where(row => row.GetProperty("name").GetString() == CollisionName).ToList();
            colliding.Count.ShouldBe(2);
            colliding.ForEach(row => row.GetProperty("nameCollision").GetBoolean().ShouldBeTrue());
            rows.Single(row => row.GetProperty("name").GetString() == "unique")
                .TryGetProperty("nameCollision", out _)
                .ShouldBeFalse();
        }
        finally
        {
            Clean(home, rootA, rootB);
        }
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
