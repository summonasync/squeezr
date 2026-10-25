using Shouldly;
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
    private readonly List<string> _created = [];

    public DoctorRunnerTests()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");
    }

    public void Dispose()
    {
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
