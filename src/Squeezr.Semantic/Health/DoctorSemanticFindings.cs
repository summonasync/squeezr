using Squeezr.Core.Configuration;
using Squeezr.Core.Health;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Semantic.Health;

// The coverage findings DoctorRunner used to derive inline, moved next to the facts they read. The
// runner hands over the workspace it already opened, the status it already built, and the facts the
// CLI probed; everything here answers from those three inputs plus the workspace's own config.
public static class DoctorSemanticFindings
{
    public const string IndexCheckProviderRemedy = "squeezr index — then check the embeddings provider line";

    public static IReadOnlyList<DoctorFinding> Collect(string rootPath, WorkspaceStatus status, WorkspaceSemanticFacts? facts)
    {
        if (facts is null)
        {
            return [];
        }

        var findings = new List<DoctorFinding>();
        if (facts.Health.ModelProblem is { } problem)
        {
            findings.Add(new DoctorFinding("embeddings provider", DoctorRunner.StateFail, problem, null));
            return findings;
        }

        var config = TryLoadConfig(rootPath);
        var chunks = status.Repos.Sum(r => r.Chunks);
        if (config?.Indexing.NoSemantic == true || chunks == 0)
        {
            return findings;
        }

        if (facts.Health.ModelKey is { } modelKey)
        {
            findings.Add(new DoctorFinding(
                "embedding model", DoctorRunner.StateInfo, ModelVintageDetail(modelKey, facts.Health.OtherModelChunks), null));
        }

        if (facts.UnavailableReason is { } unavailable)
        {
            findings.Add(new DoctorFinding("semantic search", DoctorRunner.StateInfo, $"facts unavailable: {unavailable}", null));
            return findings;
        }

        if (facts.Health.CpuOnly is { } cpu)
        {
            findings.Add(new DoctorFinding("embeddings provider", DoctorRunner.StateInfo, cpu, null));
        }

        if (facts.Qdrant is not { } qdrant)
        {
            return findings;
        }

        if (!qdrant.Available)
        {
            findings.Add(new DoctorFinding(
                "semantic search", DoctorRunner.StateFail,
                $"qdrant unavailable — {chunks} chunk(s) without vectors, {qdrant.PendingChunks} pending",
                DoctorRemedies.ServicesUp));
            return findings;
        }

        if ((qdrant.Points ?? 0) == 0)
        {
            findings.Add(new DoctorFinding(
                "semantic search", DoctorRunner.StateFail,
                $"semantic coverage 0% — {chunks} chunk(s) indexed, {qdrant.PendingChunks} pending, no vectors in the store",
                IndexCheckProviderRemedy));
            return findings;
        }

        // one watch drain pass moves at most watch.embedBudgetPerPass chunks, so a backlog at or
        // above that line has already outlived a full drain pass and is not transient catch-up work
        var pendingThreshold = config?.Watch.EmbedBudgetPerPass ?? WatchConfig.DefaultEmbedBudgetPerPass;
        if (qdrant.PendingChunks >= pendingThreshold)
        {
            findings.Add(new DoctorFinding(
                "semantic search", DoctorRunner.StateFail,
                $"{qdrant.PendingChunks} chunk(s) pending vectors (threshold {pendingThreshold}{ThresholdOrigin(pendingThreshold)})",
                DoctorRemedies.Index));
        }

        return findings;
    }

    private static string ModelVintageDetail(string modelKey, int? otherModelChunks) =>
        otherModelChunks is null
            ? $"{modelKey} (vintage count unavailable)"
            : $"{modelKey} — {otherModelChunks} chunk(s) carry vectors from another embedding model";

    private static string ThresholdOrigin(int pendingThreshold)
        => pendingThreshold == WatchConfig.DefaultEmbedBudgetPerPass ? string.Empty : ", watch.embedBudgetPerPass";

    private static SqueezrConfig? TryLoadConfig(string rootPath)
    {
        try
        {
            return SemanticConfigFile.Load(rootPath).Core;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // an unreadable config means no semantic judgment; the workspace-level checks already
            // report the broken store, and the coverage arm may add its own honest fail on top
            return null;
        }
    }
}
