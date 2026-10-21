using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

// The stale verdict on the usage surface must be the same fact `workspace list` prints: both consume
// OrphanScanner's classification, so a dead root shows up as (stale) in both outputs or in neither.
// The fixture's queries.jsonl lives in the central store dir, so the telemetry outlives the deleted
// root exactly the way a real dead workspace's does. The log line must be single-line JSON — the
// reader splits on newlines and silently drops fragments of a pretty-printed entry.
[Collection("ProcessGlobals")]
public sealed class UsageStaleVerdictCliTests
{
    [Fact]
    public async Task UsageAndList_NameTheIdenticalSetOfDeadStoreKeys()
    {
        var home = TestDirs.NewTempDir();
        var liveRoot = TestDirs.NewTempDir();
        var deadRoot = TestDirs.NewTempDir();
        try
        {
            var deadKey = WriteWorkspace(home, "usage-dead", deadRoot);
            var liveKey = WriteWorkspace(home, "usage-live", liveRoot);
            Directory.Delete(deadRoot, recursive: true);

            var usage = await CliTestProcess.RunWithHomeAsync(home, TestDirs.NewTempDir(), "usage", "--json");
            var listed = await CliTestProcess.RunWithHomeAsync(home, TestDirs.NewTempDir(), "workspace", "list", "--json");

            usage.ExitCode.ShouldBe(0);
            listed.ExitCode.ShouldBe(0);
            UsageDeadKeys(usage.StdOut).ShouldBe([deadKey]);
            ListDeadKeys(listed.StdOut).ShouldBe([deadKey]);
        }
        finally
        {
            Clean(home, liveRoot, deadRoot);
        }
    }

    [Fact]
    public async Task Usage_KeepsWordingAndJsonShape_ForTheMovedVerdict()
    {
        var home = TestDirs.NewTempDir();
        var liveRoot = TestDirs.NewTempDir();
        var deadRoot = TestDirs.NewTempDir();
        try
        {
            var deadKey = WriteWorkspace(home, "usage-dead", deadRoot);
            WriteWorkspace(home, "usage-live", liveRoot);
            Directory.Delete(deadRoot, recursive: true);

            var human = await CliTestProcess.RunWithHomeAsync(home, TestDirs.NewTempDir(), "usage");
            var json = await CliTestProcess.RunWithHomeAsync(home, TestDirs.NewTempDir(), "usage", "--json");

            human.ExitCode.ShouldBe(0);
            human.StdOut.ShouldContain("(stale)");
            json.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(json.StdOut);
            var dead = document.RootElement
                .GetProperty("workspaces")
                .EnumerateArray()
                .Single(row => row.GetProperty("storeKey").GetString() == deadKey);
            dead.GetProperty("stale").ValueKind.ShouldBe(JsonValueKind.True);
            dead.GetProperty("rootPath").GetString().ShouldBe(WorkspaceCanonicalPath.Canonicalize(deadRoot));
        }
        finally
        {
            Clean(home, liveRoot, deadRoot);
        }
    }

    private static List<string> UsageDeadKeys(string stdOut)
    {
        using var document = JsonDocument.Parse(stdOut.Trim());
        return DeadKeys(document.RootElement.GetProperty("workspaces").EnumerateArray());
    }

    private static List<string> ListDeadKeys(string stdOut)
    {
        var rows = stdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();
        return DeadKeys(rows);
    }

    private static List<string> DeadKeys(IEnumerable<JsonElement> rows) =>
        rows
            .Where(row => row.TryGetProperty("stale", out var stale) && stale.GetBoolean())
            .Select(row => row.GetProperty("storeKey").GetString()!)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

    private static string WriteWorkspace(string home, string name, string root)
    {
        var canonical = WorkspaceCanonicalPath.Canonicalize(root);
        var storeKey = WorkspaceStoreKeys.Create(name, canonical);
        var storeDir = Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey);
        Directory.CreateDirectory(storeDir);
        var entry = new QueryTelemetryEntry(
            DateTime.UtcNow.AddMinutes(-1), Guid.NewGuid(), "search", null, 10, false, 0, 1, false, null, "cli", null);
        File.WriteAllLines(
            Path.Combine(storeDir, SqueezrPaths.QueriesLogFileName),
            [JsonSerializer.Serialize(entry)]);

        var registryPath = Path.Combine(home, SqueezrPaths.RegistryFileName);
        var registry = File.Exists(registryPath)
            ? JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(File.ReadAllText(registryPath), SqueezrJson.FileOptions)!
            : [];
        registry.Add(new WorkspaceRegistryEntry(name, canonical, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        File.WriteAllText(registryPath, JsonSerializer.Serialize(registry, SqueezrJson.FileOptions));

        return storeKey;
    }

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
