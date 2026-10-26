using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Health;

public sealed record DoctorFinding(string Name, string State, string Detail, string? Remedy = null);

public sealed record DoctorReport(bool Healthy, IReadOnlyList<DoctorFinding> Findings);

public static class DoctorRemedies
{
    public const string Index = "squeezr index";
    public const string WorkspaceRepair = "squeezr workspace repair";
    public const string ServicesUp = "squeezr services up";
    public const string FleetRestart = "squeezr fleet restart";
}

public static class DoctorRunner
{
    public const string StateFail = "fail";
    public const string StateInfo = "info";
    public static readonly TimeSpan StaleIndexThreshold = TimeSpan.FromDays(30);

    // findings carry only problems and info; "ok" is the absence of a line, and Healthy is no fail among them
    public static DoctorReport CollectWorkspaceFindings(DateTime utcNow, Func<int, bool>? processAlive = null)
    {
        var findings = new List<DoctorFinding>();
        foreach (var entry in WorkspaceRegistry.Load())
        {
            findings.AddRange(WorkspaceFindings(entry, utcNow, processAlive));
        }

        findings.AddRange(OrphanFindings());
        return Report(findings);
    }

    private static IReadOnlyList<DoctorFinding> WorkspaceFindings(
        WorkspaceRegistryEntry entry, DateTime utcNow, Func<int, bool>? processAlive)
    {
        var findings = new List<DoctorFinding>();
        if (!Directory.Exists(entry.RootPath))
        {
            findings.Add(new DoctorFinding(
                $"workspace {entry.Name}", StateFail,
                $"root path no longer exists: {entry.RootPath}", DoctorRemedies.WorkspaceRepair));
            return findings;
        }

        WorkspaceStatus? status = null;
        try
        {
            status = new WorkspaceManager(entry.RootPath).GetStatus();
        }
        catch (WorkspaceException ex)
        {
            findings.Add(new DoctorFinding(
                $"workspace {entry.Name}", StateFail, $"status unavailable: {ex.Message}", DoctorRemedies.Index));
        }

        if (status is null)
        {
            return findings;
        }

        if (status.Repos.Count == 0)
        {
            findings.Add(new DoctorFinding(
                $"workspace {entry.Name}", StateFail, "no repositories indexed", DoctorRemedies.Index));
            return findings;
        }

        foreach (var repo in status.Repos)
        {
            if (repo.LastIndexedAt is null)
            {
                findings.Add(new DoctorFinding(
                    $"repo {repo.Name}", StateFail, "never indexed", DoctorRemedies.Index));
                continue;
            }

            var age = utcNow - repo.LastIndexedAt.Value.ToUniversalTime();
            if (age > StaleIndexThreshold)
            {
                findings.Add(new DoctorFinding(
                    $"repo {repo.Name}", StateInfo,
                    $"index is {(int)age.TotalDays} days old ({repo.Files} files)", null));
            }
        }

        findings.AddRange(DaemonFindings(entry, utcNow, processAlive));
        return findings;
    }

    private static IEnumerable<DoctorFinding> DaemonFindings(
        WorkspaceRegistryEntry entry, DateTime utcNow, Func<int, bool>? processAlive)
    {
        var storeDir = SqueezrPaths.StoreDir(entry.RootPath);
        foreach (var beat in DaemonHeartbeatReader.TryReadAll(storeDir))
        {
            if (DaemonHeartbeatReader.IsAlive(beat, utcNow, processAlive))
            {
                yield return new DoctorFinding(
                    $"daemon {beat.Kind}", StateInfo,
                    $"running (pid {beat.Pid}, host {beat.Host})", null);
            }
            else
            {
                var idle = utcNow - beat.LastBeatAt;
                yield return new DoctorFinding(
                    $"daemon {beat.Kind}", StateFail,
                    $"stale heartbeat (pid {beat.Pid}, last beat {(int)idle.TotalMinutes}m ago)",
                    DoctorRemedies.FleetRestart);
            }
        }
    }

    private static IEnumerable<DoctorFinding> OrphanFindings()
    {
        var scan = OrphanScanner.Scan();
        if (scan.Orphans.Count > 0)
        {
            yield return new DoctorFinding(
                "orphan stores", StateFail,
                $"{scan.Orphans.Count} unregistered store(s): {Names(scan.Orphans.Select(o => o.Name))}",
                DoctorRemedies.WorkspaceRepair);
        }

        if (scan.Husks.Count > 0)
        {
            yield return new DoctorFinding(
                "husk workspaces", StateFail,
                $"{scan.Husks.Count} workspace(s) with missing roots: {Names(scan.Husks.Select(h => h.Name))}",
                DoctorRemedies.WorkspaceRepair);
        }

        if (scan.Stale.Count > 0)
        {
            yield return new DoctorFinding(
                "stale registry entries", StateFail,
                $"{scan.Stale.Count} entry(ies) point at gone roots: {Names(scan.Stale.Select(s => s.Name))}",
                DoctorRemedies.WorkspaceRepair);
        }
    }

    private static string Names(IEnumerable<string> names) => string.Join(", ", names);

    public static DoctorReport Report(IReadOnlyList<DoctorFinding> findings) =>
        new(findings.All(f => f.State != StateFail), findings);
}
