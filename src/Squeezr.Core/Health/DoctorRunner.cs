using System.Globalization;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Health;

public sealed record DoctorFinding(string Name, string State, string Detail, string? Remedy = null);

public sealed record DoctorReport(bool Healthy, IReadOnlyList<DoctorFinding> Findings);

public static class DoctorRemedies
{
    public const string Index = "squeezr index";
    public const string IndexCheckProvider = "squeezr index — then check the embeddings provider line";
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
    public static DoctorReport CollectWorkspaceFindings(
        DateTime utcNow,
        Func<int, bool>? processAlive = null,
        Func<string, WorkspaceSemanticFacts>? semanticFacts = null)
    {
        var findings = new List<DoctorFinding>();
        foreach (var entry in WorkspaceRegistry.Load())
        {
            findings.AddRange(WorkspaceFindings(entry, utcNow, processAlive, semanticFacts));
        }

        findings.AddRange(OrphanFindings());
        findings.Add(FixtureFinding(utcNow));
        return Report(findings);
    }

    private static IReadOnlyList<DoctorFinding> WorkspaceFindings(
        WorkspaceRegistryEntry entry,
        DateTime utcNow,
        Func<int, bool>? processAlive,
        Func<string, WorkspaceSemanticFacts>? semanticFacts)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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

        findings.AddRange(SemanticCoverageFindings(entry, status, semanticFacts?.Invoke(entry.RootPath)));
        findings.AddRange(DaemonFindings(entry, utcNow, processAlive));
        return findings;
    }

    private static IEnumerable<DoctorFinding> SemanticCoverageFindings(
        WorkspaceRegistryEntry entry,
        WorkspaceStatus status,
        WorkspaceSemanticFacts? facts)
    {
        if (facts is null)
        {
            yield break;
        }

        var config = TryLoadConfig(entry);
        var chunks = status.Repos.Sum(r => r.Chunks);
        if (config?.Indexing.NoSemantic == true || chunks == 0)
        {
            yield break;
        }

        if (facts.UnavailableReason is { } unavailable)
        {
            yield return new DoctorFinding("semantic search", StateInfo, $"facts unavailable: {unavailable}", null);
            yield break;
        }

        if (facts.Health.ModelProblem is { } problem)
        {
            yield return new DoctorFinding("embeddings provider", StateFail, problem, null);
        }

        if (facts.Health.FallbackReason is { } reason)
        {
            yield return new DoctorFinding(
                "embeddings provider", StateInfo,
                $"auto resolved to {facts.Health.Provider} (ollama unreachable: {reason})", null);
        }

        if (facts.Health.CpuOnly is { } cpu)
        {
            yield return new DoctorFinding("embeddings provider", StateInfo, cpu, null);
        }

        if (facts.Qdrant is not { } qdrant)
        {
            yield break;
        }

        if (!qdrant.Available)
        {
            yield return new DoctorFinding(
                "semantic search", StateFail,
                $"qdrant unavailable — {chunks} chunk(s) without vectors, {qdrant.PendingChunks} pending",
                DoctorRemedies.ServicesUp);
            yield break;
        }

        if ((qdrant.Points ?? 0) == 0)
        {
            yield return new DoctorFinding(
                "semantic search", StateFail,
                $"semantic coverage 0% — {chunks} chunk(s) indexed, {qdrant.PendingChunks} pending, no vectors in the store",
                DoctorRemedies.IndexCheckProvider);
            yield break;
        }

        // one watch drain pass moves at most watch.embedBudgetPerPass chunks, so a backlog at or
        // above that line has already outlived a full drain pass and is not transient catch-up work
        var pendingThreshold = config?.Watch.EmbedBudgetPerPass ?? WatchConfig.DefaultEmbedBudgetPerPass;
        if (qdrant.PendingChunks >= pendingThreshold)
        {
            yield return new DoctorFinding(
                "semantic search", StateFail,
                $"{qdrant.PendingChunks} chunk(s) pending vectors (threshold {pendingThreshold}{ThresholdOrigin(pendingThreshold)})",
                DoctorRemedies.Index);
        }
    }

    private static string ThresholdOrigin(int pendingThreshold)
        => pendingThreshold == WatchConfig.DefaultEmbedBudgetPerPass ? string.Empty : ", watch.embedBudgetPerPass";

    private static SqueezrConfig? TryLoadConfig(WorkspaceRegistryEntry entry)
    {
        try
        {
            return ConfigFile.Load(entry.RootPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // an unreadable config means no semantic judgment; the workspace-level checks already
            // report the broken store, and the coverage arm may add its own honest fail on top
            return null;
        }
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

    // The residue line is intentionally read-only and always informational: a tmpfs full of test
    // fixtures is worth seeing before it eats the box, but doctor itself must not clean or fail.
    public static DoctorFinding FixtureFinding(DateTime utcNow, string? fixtureRoot = null)
    {
        var root = fixtureRoot ?? Path.Combine(Path.GetTempPath(), "squeezr-tests");
        if (!Directory.Exists(root))
        {
            return new DoctorFinding("test fixtures", StateInfo, $"no fixture root at {root}", null);
        }

        try
        {
            var leaves = Directory.GetDirectories(root);
            if (leaves.Length == 0)
            {
                return new DoctorFinding("test fixtures", StateInfo, $"no fixture dirs under {root}", null);
            }

            long totalBytes = 0;
            var oldest = utcNow;
            foreach (var leaf in leaves)
            {
                totalBytes += LeafBytes(leaf);
                var modified = Directory.GetLastWriteTimeUtc(leaf);
                if (modified < oldest)
                {
                    oldest = modified;
                }
            }

            return new DoctorFinding(
                "test fixtures", StateInfo,
                $"{leaves.Length} fixture dir(s) under {root}, {FormatBytes(totalBytes)} total, " +
                $"oldest leaf {Math.Max(0, (int)(utcNow - oldest).TotalHours)}h old",
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DoctorFinding("test fixtures", StateInfo, $"fixture scan incomplete: {ex.Message}", null);
        }
    }

    private static long LeafBytes(string leaf) =>
        Directory.EnumerateFiles(leaf, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        }).Sum(file => new FileInfo(file).Length);

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        var unit = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} B"
            : value.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static DoctorReport Report(IReadOnlyList<DoctorFinding> findings) =>
        new(findings.All(f => f.State != StateFail), findings);
}
