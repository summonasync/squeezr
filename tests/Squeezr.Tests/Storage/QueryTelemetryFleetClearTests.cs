using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

[Collection("ProcessGlobals")]
public sealed class QueryTelemetryFleetClearTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public QueryTelemetryFleetClearTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    [Fact]
    public void Clear_MultipleRegisteredStores_RemovesEachLogAndCountsLines()
    {
        var first = RegisteredWorkspace("first", recordCount: 3);
        var second = RegisteredWorkspace("second", recordCount: 2);

        var result = QueryTelemetryFleetClear.Clear();

        result.Cleared.Count.ShouldBe(2);
        result.Skipped.ShouldBeEmpty();
        result.TotalRemoved.ShouldBe(5);
        result.Cleared.ShouldContain(item => item.StoreKey == first.StoreKey && item.RecordsRemoved == 3);
        result.Cleared.ShouldContain(item => item.StoreKey == second.StoreKey && item.RecordsRemoved == 2);
        File.Exists(SqueezrPaths.QueriesLogPath(first.Root)).ShouldBeFalse();
        File.Exists(SqueezrPaths.QueriesLogPath(second.Root)).ShouldBeFalse();
        QueryTelemetry.Count(first.Root).ShouldBe(0);

        File.Exists(Path.Combine(first.StoreDir, SqueezrPaths.MetaDbFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(first.StoreDir, SqueezrPaths.ConfigFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(first.StoreDir, SqueezrPaths.TrigramIndexFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(first.StoreDir, "canary.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(second.StoreDir, "canary.txt")).ShouldBeTrue();
    }

    [Fact]
    public void Clear_StaleRow_SkipsItAndClearsTheLiveFleet()
    {
        var gone = RegisteredWorkspace("gone", recordCount: 1);
        Directory.Delete(gone.Root, recursive: true);
        var live = RegisteredWorkspace("live", recordCount: 2);

        var result = QueryTelemetryFleetClear.Clear();

        result.Cleared.ShouldHaveSingleItem().StoreKey.ShouldBe(live.StoreKey);
        result.Cleared.ShouldHaveSingleItem().RecordsRemoved.ShouldBe(2);
        result.Skipped.ShouldHaveSingleItem().StoreKey.ShouldBe(gone.StoreKey);
        result.Skipped.ShouldHaveSingleItem().Reason.ShouldNotBeEmpty();
        result.TotalRemoved.ShouldBe(2);
        File.Exists(SqueezrPaths.QueriesLogPath(live.Root)).ShouldBeFalse();
    }

    [Fact]
    public void Clear_EmptyRegistry_ReturnsAnEmptyResult()
    {
        var result = QueryTelemetryFleetClear.Clear();

        result.Cleared.ShouldBeEmpty();
        result.Skipped.ShouldBeEmpty();
        result.TotalRemoved.ShouldBe(0);
    }

    [Fact]
    public void Clear_StoreWithoutTelemetryFile_ReportsZeroAndCreatesNothing()
    {
        var workspace = RegisteredWorkspace("quiet", recordCount: 0);
        File.Exists(SqueezrPaths.QueriesLogPath(workspace.Root)).ShouldBeFalse();

        var result = QueryTelemetryFleetClear.Clear();

        result.Cleared.ShouldHaveSingleItem().RecordsRemoved.ShouldBe(0);
        File.Exists(SqueezrPaths.QueriesLogPath(workspace.Root)).ShouldBeFalse();
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

    private RegisteredStore RegisteredWorkspace(string name, int recordCount)
    {
        var root = TestDirs.NewTempDir();
        _createdDirs.Add(root);
        var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
        var storeKey = WorkspaceStoreKeys.Create(name, canonicalRoot);
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            name, canonicalRoot, storeKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var storeDir = SqueezrPaths.StoreDir(root);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName), "index bytes");
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.ConfigFileName), "{}");
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.TrigramIndexFileName), "index bytes");
        File.WriteAllText(Path.Combine(storeDir, "canary.txt"), "keep me");
        for (var index = 0; index < recordCount; index++)
        {
            QueryTelemetry.Record(root, Guid.NewGuid(), "search", new { query = $"q{index}" }, 120, false, 1, 5);
        }

        return new RegisteredStore(root, storeDir, storeKey);
    }

    private sealed record RegisteredStore(string Root, string StoreDir, string StoreKey);
}
