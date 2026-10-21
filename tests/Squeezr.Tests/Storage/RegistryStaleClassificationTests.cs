using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed class RegistryStaleClassificationTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LiveName = "cls-live";
    private const string DeadWithStoreName = "cls-dead-store";
    private const string DeadWithoutStoreName = "cls-dead-nostore";
    private const string HuskName = "cls-husk";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];
    private readonly string _liveKey;
    private readonly string _deadWithStoreKey;
    private readonly string _deadWithoutStoreKey;
    private readonly string _huskKey;
    private readonly IReadOnlyList<string> _deadKeys;
    private readonly string _cwd;

    public RegistryStaleClassificationTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = NewTempDir();
        _cwd = NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();

        var liveRoot = Canonical(NewTempDir());
        var deadWithStoreRoot = Canonical(NewTempDir());
        var deadWithoutStoreRoot = Canonical(NewTempDir());
        var huskRoot = Canonical(SqueezrHome.Resolve());

        _liveKey = StoreKey(LiveName, liveRoot);
        _deadWithStoreKey = StoreKey(DeadWithStoreName, deadWithStoreRoot);
        _deadWithoutStoreKey = StoreKey(DeadWithoutStoreName, deadWithoutStoreRoot);
        _huskKey = StoreKey(HuskName, huskRoot);
        _deadKeys = SortedKeys([_deadWithStoreKey, _deadWithoutStoreKey]);

        WriteStoreDir(_deadWithStoreKey, DeadWithStoreName, deadWithStoreRoot);

        WriteRegistry(
            Row(LiveName, liveRoot, _liveKey),
            Row(DeadWithStoreName, deadWithStoreRoot, _deadWithStoreKey),
            Row(DeadWithoutStoreName, deadWithoutStoreRoot, _deadWithoutStoreKey),
            Row(HuskName, huskRoot, _huskKey));

        Directory.Delete(deadWithStoreRoot, recursive: true);
        Directory.Delete(deadWithoutStoreRoot, recursive: true);
    }

    [Fact]
    public async Task FourCategoryFixture_ListFleetRepairGiveIdenticalStaleVerdicts()
    {
        var scan = OrphanScanner.Scan();
        SortedKeys(scan.Stale.Select(stale => stale.StoreKey)).ShouldBe(_deadKeys);
        scan.Husks.ShouldHaveSingleItem().StoreKey.ShouldBe(_huskKey);

        var outcome = RegistryRepair.AdoptAll();
        SortedKeys(outcome.Stale.Select(stale => stale.StoreKey)).ShouldBe(_deadKeys);
        outcome.Adopted.ShouldBeEmpty();
        outcome.NeedsRoot.ShouldBeEmpty();

        var listed = await ReadVerdictsAsync("workspace", "list", "--json");
        var fleet = await ReadVerdictsAsync("fleet", "--json");

        SortedKeys(listed.Where(pair => pair.Value).Select(pair => pair.Key)).ShouldBe(_deadKeys);
        SortedKeys(fleet.Where(pair => pair.Value).Select(pair => pair.Key)).ShouldBe(_deadKeys);

        foreach (var verdicts in new[] { listed, fleet })
        {
            verdicts[_liveKey].ShouldBeFalse();
            verdicts[_huskKey].ShouldBeFalse();
            verdicts[_deadWithStoreKey].ShouldBeTrue();
            verdicts[_deadWithoutStoreKey].ShouldBeTrue();
        }
    }

    [Fact]
    public async Task HumanOutput_MarksStaleRows_AndWarnsWithDropHint()
    {
        var fleet = await RunCliAsync("fleet");

        fleet.ExitCode.ShouldBe(0);
        fleet.StdOut.ShouldContain("stale");
        fleet.StdErr.ShouldContain($"workspace '{DeadWithStoreName}' is stale");
        fleet.StdErr.ShouldContain($"workspace '{DeadWithoutStoreName}' is stale");
        fleet.StdErr.ShouldNotContain($"workspace '{LiveName}' is stale");
        fleet.StdErr.ShouldNotContain($"workspace '{HuskName}' is stale");

        var listed = await RunCliAsync("workspace", "list");
        listed.ExitCode.ShouldBe(0);
        listed.StdOut.ShouldContain("stale");
        listed.StdErr.ShouldContain("squeezr drop --store-key");
    }

    [Fact]
    public async Task ListJson_KeepsFieldNames_AndFlagsOnlyTheHuskRow()
    {
        var result = await RunCliAsync("workspace", "list", "--json");

        result.ExitCode.ShouldBe(0);
        var rows = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        rows.Count.ShouldBe(4);
        rows.Where(row => !row.TryGetProperty("stale", out _)).ToList().ShouldBeEmpty();

        var huskRow = rows.Single(row => row.GetProperty("storeKey").GetString() == _huskKey);
        huskRow.GetProperty("husk").GetBoolean().ShouldBeTrue();
        rows.Single(row => row.GetProperty("storeKey").GetString() == _liveKey)
            .TryGetProperty("husk", out _).ShouldBeFalse();
    }

    [Fact]
    public void AdoptAll_ReportsStaleRows_AndKeepsThemInTheRegistry()
    {
        var before = WorkspaceRegistry.Load();

        var outcome = RegistryRepair.AdoptAll();

        SortedKeys(outcome.Stale.Select(stale => stale.StoreKey))
            .ShouldBe(_deadKeys);
        outcome.Stale.ShouldContain(stale =>
            stale.StoreKey == _deadWithStoreKey &&
            stale.Name == DeadWithStoreName &&
            stale.RootPath.Length > 0);

        var after = WorkspaceRegistry.Load();
        after.Count.ShouldBe(before.Count);
        SortedKeys(after.Select(row => row.StoreKey))
            .ShouldBe(SortedKeys(before.Select(row => row.StoreKey)));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static string StoreKey(string name, string canonicalRoot) =>
        WorkspaceStoreKeys.Create(name, canonicalRoot);

    private static WorkspaceRegistryEntry Row(string name, string canonicalRoot, string storeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(name, canonicalRoot, storeKey, now, now);
    }

    private static IReadOnlyList<string> SortedKeys(IEnumerable<string> keys) =>
        keys.OrderBy(key => key, StringComparer.Ordinal).ToList();

    private string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);

    private string NewTempDir()
    {
        var dir = TestDirs.NewTempDir();
        _createdDirs.Add(dir);
        return dir;
    }

    private void WriteStoreDir(string storeKey, string name, string canonicalRoot)
    {
        var storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(
                new WorkspaceManifest { Name = name, RootPath = canonicalRoot },
                SqueezrJson.FileOptions));
    }

    private void WriteRegistry(params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            WorkspaceRegistry.FilePath,
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private async Task<Dictionary<string, bool>> ReadVerdictsAsync(params string[] args)
    {
        var result = await RunCliAsync(args);

        result.ExitCode.ShouldBe(0);
        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Select(entry => (
                StoreKey: entry.GetProperty("storeKey").GetString()!,
                Stale: entry.GetProperty("stale").GetBoolean()))
            .ToDictionary(pair => pair.StoreKey, pair => pair.Stale);
    }

    private Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args) =>
        CliTestProcess.RunWithHomeAsync(_home, _cwd, args);
}
