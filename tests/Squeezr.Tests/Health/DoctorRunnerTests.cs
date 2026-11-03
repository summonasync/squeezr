using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Health;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Health;

[Collection("ProcessGlobals")]
public sealed class DoctorRunnerTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string Source = "public class Calc { public int Add(int a, int b) => a + b; }";

    private readonly string _home = TestDirs.NewTempDir();
    private readonly string? _previousEnv;
    private readonly List<string> _created = [];

    public DoctorRunnerTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _created)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void IndexedWorkspace_Alone_IsHealthy()
    {
        var root = NewIndexedWorkspace();

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true);

        report.Healthy.ShouldBeTrue();
        report.Findings.ShouldNotContain(f => f.State == DoctorRunner.StateFail);
    }

    [Fact]
    public void GoneRoot_ProducesFailWithRepairRemedy()
    {
        var gone = NewTempDir();
        Directory.Delete(gone);
        SeedRegistry(("gone", gone));

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true);

        report.Healthy.ShouldBeFalse();
        var finding = report.Findings.Single(f => f.Name == "workspace gone");
        finding.State.ShouldBe(DoctorRunner.StateFail);
        finding.Remedy.ShouldBe(DoctorRemedies.WorkspaceRepair);
    }

    [Fact]
    public void UnindexedWorkspace_ProducesFailWithIndexRemedy()
    {
        var root = NewInitOnlyWorkspace();

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true);

        report.Healthy.ShouldBeFalse();
        var finding = report.Findings.Single(f => f.State == DoctorRunner.StateFail);
        finding.Remedy.ShouldBe(DoctorRemedies.Index);
    }

    [Fact]
    public void StaleDaemonHeartbeat_ProducesFailWithFleetRestartRemedy()
    {
        var root = NewIndexedWorkspace();
        WriteStaleBeat(root);

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => false);

        report.Healthy.ShouldBeFalse();
        var finding = report.Findings.Single(f => f.Name.StartsWith("daemon", StringComparison.Ordinal));
        finding.State.ShouldBe(DoctorRunner.StateFail);
        finding.Remedy.ShouldBe(DoctorRemedies.FleetRestart);
    }

    [Fact]
    public void DoctorRun_IsReadOnly_NoTelemetryAndNoMtimeChange()
    {
        var root = NewIndexedWorkspace();
        var before = StoreFilesSnapshot(root);

        DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true);

        StoreFilesSnapshot(root).ShouldBe(before);
        QueryTelemetry.Count(root).ShouldBe(0);
    }

    [Fact]
    public void Report_HealthyOnlyWithoutFails()
    {
        DoctorRunner.Report([new DoctorFinding("age", DoctorRunner.StateInfo, "old index")])
            .Healthy.ShouldBeTrue();
        DoctorRunner.Report([new DoctorFinding("x", DoctorRunner.StateFail, "boom")])
            .Healthy.ShouldBeFalse();
    }

    [Fact]
    public void SemanticFacts_QdrantUnavailableWithChunks_FailsWithServicesRemedy()
    {
        _ = NewIndexedWorkspace();

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                new QdrantStatus(false, null, 12, 12), EmbeddingsHealth.Silent));

        report.Healthy.ShouldBeFalse();
        var finding = report.Findings.Single(f => f.Name == "semantic search");
        finding.State.ShouldBe(DoctorRunner.StateFail);
        finding.Remedy.ShouldBe(DoctorRemedies.ServicesUp);
        finding.Detail.ShouldContain("chunk(s) without vectors");
    }

    [Fact]
    public void SemanticFacts_CoverageZero_FailsWithProviderLineRemedy()
    {
        _ = NewIndexedWorkspace();

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                new QdrantStatus(true, 0, 4, 4), EmbeddingsHealth.Silent));

        report.Healthy.ShouldBeFalse();
        var finding = report.Findings.Single(f => f.Name == "semantic search");
        finding.Remedy.ShouldBe(DoctorRemedies.IndexCheckProvider);
        finding.Detail.ShouldContain("coverage 0%");
    }

    [Fact]
    public void SemanticFacts_PendingAboveThreshold_FailsWithIndexRemedy()
    {
        _ = NewIndexedWorkspace();

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                new QdrantStatus(true, 100, 400, 500), EmbeddingsHealth.Silent));

        report.Healthy.ShouldBeFalse();
        var finding = report.Findings.Single(f => f.Name == "semantic search");
        finding.Remedy.ShouldBe(DoctorRemedies.Index);
        finding.Detail.ShouldContain("pending vectors");
        finding.Detail.ShouldContain(WatchConfig.DefaultEmbedBudgetPerPass.ToString());
        finding.Detail.ShouldNotContain("watch.embedBudgetPerPass");
    }

    [Fact]
    public void SemanticFacts_LoweredEmbedBudget_FailsAtFewerPending()
    {
        var root = NewIndexedWorkspace();
        var config = ConfigFile.Load(root);
        ConfigFile.Save(root, config with { Watch = config.Watch with { EmbedBudgetPerPass = 8 } });

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                new QdrantStatus(true, 2, 10, 12), EmbeddingsHealth.Silent));

        report.Healthy.ShouldBeFalse();
        var finding = report.Findings.Single(f => f.Name == "semantic search");
        finding.Remedy.ShouldBe(DoctorRemedies.Index);
        finding.Detail.ShouldContain("threshold 8");
        finding.Detail.ShouldContain("watch.embedBudgetPerPass");
    }

    [Fact]
    public void SemanticFacts_BrokenConfig_FallsBackToDefaultThreshold()
    {
        var root = NewIndexedWorkspace();
        File.WriteAllText(SqueezrPaths.ConfigPath(root), "{ not json");

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                new QdrantStatus(true, 2, 300, 312), EmbeddingsHealth.Silent));

        var finding = report.Findings.SingleOrDefault(f => f.Name == "semantic search" && f.State == DoctorRunner.StateFail);
        finding.ShouldNotBeNull();
        finding.Detail.ShouldContain(WatchConfig.DefaultEmbedBudgetPerPass.ToString());
    }

    [Fact]
    public void SemanticFacts_HealthyCoverageAndProvider_StayQuiet()
    {
        _ = NewIndexedWorkspace();

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                new QdrantStatus(true, 498, 2, 500),
                new EmbeddingsHealth("ollama", null, null, null)));

        report.Healthy.ShouldBeTrue();
        report.Findings.ShouldNotContain(f => f.Name == "semantic search" || f.Name == "embeddings provider");
    }

    [Fact]
    public void SemanticFacts_UnavailableReason_InfoInsteadOfQdrantArms()
    {
        _ = NewIndexedWorkspace();

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                null, EmbeddingsHealth.Silent, "qdrant endpoint is not a valid uri"));

        var finding = report.Findings.Single(f => f.Name == "semantic search");
        finding.State.ShouldBe(DoctorRunner.StateInfo);
        finding.Detail.ShouldContain("facts unavailable");
        finding.Detail.ShouldContain("not a valid uri");
    }

    [Fact]
    public void SemanticFacts_NoSemanticConfig_StayQuiet()
    {
        var root = NewIndexedWorkspace();
        var config = ConfigFile.Load(root);
        ConfigFile.Save(root, config with { Indexing = config.Indexing with { NoSemantic = true } });

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                new QdrantStatus(false, null, 12, 12), EmbeddingsHealth.Silent));

        report.Findings.ShouldNotContain(f => f.Name == "semantic search" || f.Name == "embeddings provider");
    }

    [Fact]
    public void SemanticFacts_ModelProblem_TakesPrecedenceOverFallbackInfo()
    {
        _ = NewIndexedWorkspace();

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                new QdrantStatus(true, 4, 0, 4),
                new EmbeddingsHealth(
                    "ollama", "Connection refused",
                    "Embedding model not found at '/nonexistent/model.bin' and no bundled copy exists.", null)));

        report.Healthy.ShouldBeFalse();
        var problem = report.Findings.Single(
            f => f.Name == "embeddings provider" && f.State == DoctorRunner.StateFail);
        problem.Detail.ShouldContain("Embedding model not found");
        report.Findings.ShouldNotContain(f => f.Detail.Contains("auto resolved to ollama"));
    }

    [Fact]
    public void SemanticFacts_FallbackReason_AloneRendersInfo()
    {
        _ = NewIndexedWorkspace();

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                new QdrantStatus(true, 4, 0, 4),
                new EmbeddingsHealth("ollama", "Connection refused", null, null)));

        report.Healthy.ShouldBeTrue();
        var info = report.Findings.Single(f => f.Detail.Contains("auto resolved to ollama"));
        info.State.ShouldBe(DoctorRunner.StateInfo);
    }

    [Fact]
    public void SemanticFacts_CpuOnlyEmbeddings_InfoOnlyAndHealthy()
    {
        _ = NewIndexedWorkspace();

        var report = DoctorRunner.CollectWorkspaceFindings(DateTime.UtcNow, _ => true,
            _ => new WorkspaceSemanticFacts(
                new QdrantStatus(true, 4, 0, 4),
                new EmbeddingsHealth("ollama", null, null, "embedding model runs on CPU — embeddings will be slow")));

        report.Healthy.ShouldBeTrue();
        var info = report.Findings.Single(f => f.Name == "embeddings provider");
        info.State.ShouldBe(DoctorRunner.StateInfo);
        info.Detail.ShouldContain("runs on CPU");
    }

    [Fact]
    public void FixtureFinding_PlantedTree_ReportsDirsSizeAndOldestAge()
    {
        var now = DateTime.UtcNow;
        var root = NewTempDir();
        PlantFixtureLeaf(root, "stale", now - TimeSpan.FromHours(5), ("meta.db", 10));
        PlantFixtureLeaf(root, "fresh", now - TimeSpan.FromHours(2), ("a.bin", 1024), ("nested/b.bin", 2560));

        var finding = DoctorRunner.FixtureFinding(now, root);

        finding.Name.ShouldBe("test fixtures");
        finding.State.ShouldBe(DoctorRunner.StateInfo);
        finding.Remedy.ShouldBeNull();
        finding.Detail.ShouldContain("2 fixture dir(s)");
        finding.Detail.ShouldContain("3.5 KB");
        finding.Detail.ShouldContain("5h old");
        finding.Detail.ShouldContain(root);
    }

    [Fact]
    public void FixtureFinding_MissingRoot_IsGracefulInfoAndNeverFails()
    {
        var root = NewTempDir();
        Directory.Delete(root);

        var finding = DoctorRunner.FixtureFinding(DateTime.UtcNow, root);

        finding.State.ShouldBe(DoctorRunner.StateInfo);
        finding.Detail.ShouldContain("no fixture root");
        finding.Detail.ShouldContain(root);
        DoctorRunner.Report([finding]).Healthy.ShouldBeTrue();
    }

    private static void PlantFixtureLeaf(
        string root, string name, DateTime modified, params (string Relative, int Bytes)[] files)
    {
        var leaf = Directory.CreateDirectory(Path.Combine(root, name));
        foreach (var (relative, bytes) in files)
        {
            var target = Path.Combine(leaf.FullName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, new string('x', bytes));
        }

        Directory.SetLastWriteTimeUtc(leaf.FullName, modified);
    }

    private void SeedRegistry(params (string Name, string Root)[] entries)
    {
        var rows = entries.Select(e => new
        {
            name = e.Name,
            rootPath = e.Root,
            storeKey = WorkspaceStoreKeys.Create(e.Name, e.Root),
            createdAt = "2026-10-25T00:00:00Z",
            lastUsedAt = "2026-10-25T00:00:00Z",
        });
        File.WriteAllText(
            WorkspaceRegistry.FilePath,
            System.Text.Json.JsonSerializer.Serialize(rows));
    }

    private string NewIndexedWorkspace()
    {
        var root = NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "repo"));
        var manager = new WorkspaceManager(root);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(root);
        File.WriteAllText(Path.Combine(root, "repo", "Calc.cs"), Source);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(root);
        return root;
    }

    private string NewInitOnlyWorkspace()
    {
        var root = NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "repo"));
        var manager = new WorkspaceManager(root);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(root);
        File.WriteAllText(Path.Combine(root, "repo", "Calc.cs"), Source);
        manager.AddRepo("repo", "demo");
        return root;
    }

    private void WriteStaleBeat(string workspaceRoot)
    {
        var storeDir = SqueezrPaths.StoreDir(workspaceRoot);
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(storeDir));
        var now = DateTime.UtcNow;
        var beat = new DaemonHeartbeat(
            Environment.ProcessId, DaemonHeartbeat.DefaultKind, now - TimeSpan.FromMinutes(30),
            now - TimeSpan.FromMinutes(30), Environment.MachineName);
        File.WriteAllText(
            DaemonHeartbeat.BeatPathFor(storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId),
            System.Text.Json.JsonSerializer.Serialize(beat, SqueezrJson.FileOptions));
    }

    private static List<(string Path, DateTime Mtime, long Size)> StoreFilesSnapshot(string root)
    {
        var storeDir = SqueezrPaths.StoreDir(root);
        return Directory.EnumerateFiles(storeDir, "*", SearchOption.AllDirectories)
            .Select(p => (p, File.GetLastWriteTimeUtc(p), new FileInfo(p).Length))
            .OrderBy(t => t.p, StringComparer.Ordinal)
            .ToList();
    }

    private string NewTempDir()
    {
        var dir = TestDirs.NewTempDir();
        _created.Add(dir);
        return dir;
    }
}
