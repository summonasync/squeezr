using LiteDB;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Mcp;

public static class CostBaseline
{
    public static (AnswerCost Cost, int UnknownFiles) Measure(
        string root,
        IEnumerable<(string Repo, string Path)> files,
        int tokensUsed,
        bool complete)
    {
        try
        {
            using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
            return Measure(store, files, tokensUsed, complete);
        }
        catch (Exception ex) when (
            ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException
                or LiteException or InvalidOperationException)
        {
            return (AnswerCost.Unknown(tokensUsed, complete), 0);
        }
    }

    public static (AnswerCost Cost, int UnknownFiles) Measure(
        MetaStore store,
        IEnumerable<(string Repo, string Path)> files,
        int tokensUsed,
        bool complete)
    {
        var unknownFiles = 0;
        long baseline = 0;
        var repoIds = new Dictionary<string, uint?>(StringComparer.Ordinal);
        foreach (var (repo, path) in files.Distinct())
        {
            if (!repoIds.TryGetValue(repo, out var repoId))
            {
                repoId = store.FindRepo(repo)?.Id;
                repoIds[repo] = repoId;
            }

            var file = repoId is { } id ? store.FindFile(id, path) : null;
            if (file is null)
            {
                unknownFiles++;
                continue;
            }

            baseline += TokenEstimate.WholeFile(file.SizeBytes);
        }

        return baseline == 0
            ? (AnswerCost.Unknown(tokensUsed, complete), unknownFiles)
            : (AnswerCost.Measured(tokensUsed, baseline, complete), unknownFiles);
    }
}
