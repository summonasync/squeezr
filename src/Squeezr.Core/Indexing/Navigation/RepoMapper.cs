using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Navigation;

public sealed record MapLine(int Depth, string Text);

public sealed record RepoMapResult(
    IReadOnlyList<string> Repos,
    int Depth,
    IReadOnlyList<MapLine> Lines,
    int TokensUsed,
    bool Truncated,
    string? Hint);

public sealed class RepoMapper
{
    public const int DefaultDepth = 2;

    public const int DefaultMaxTokens = 4000;

    private const string TruncationHint = "lower --depth or raise --max-tokens";

    private static readonly HashSet<string> TypeKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "interface", "enum"
    };

    private readonly string _workspaceRoot;

    public RepoMapper(string workspaceRoot) => _workspaceRoot = NavigationContext.RequireIndex(workspaceRoot);

    public RepoMapResult Build(string? repo, int depth, int maxTokens)
    {
        if (depth < 1)
        {
            throw new WorkspaceException("--depth must be >= 1.");
        }

        var manifest = WorkspaceManifestFile.Load(_workspaceRoot);
        var budget = new TokenBudget(maxTokens);
        var lines = new List<MapLine>();
        var repoNames = new List<string>();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot));
        foreach (var entry in NavigationContext.SelectRepos(manifest, repo))
        {
            var repoDoc = store.FindRepo(entry.Name);
            if (repoDoc is null)
            {
                continue;
            }

            repoNames.Add(repoDoc.Name);
            if (!Take(budget, lines, 0, $"{repoDoc.Name}/"))
            {
                return Finish(repoNames, depth, lines, budget);
            }

            var files = store.ListFiles(repoDoc.Id);
            var symbolsByFile = store.ListSymbolsByRepo(repoDoc.Id)
                .GroupBy(s => s.FileId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var dir in files.GroupBy(f => DirName(f.Path)).OrderBy(g => g.Key))
            {
                if (!Take(budget, lines, 1, $"{dir.Key}/ ({dir.Count()} files)"))
                {
                    return Finish(repoNames, depth, lines, budget);
                }

                if (depth < 2)
                {
                    continue;
                }

                foreach (var file in dir.OrderBy(f => f.Path))
                {
                    if (!Take(budget, lines, 2, Path.GetFileName(file.Path)))
                    {
                        return Finish(repoNames, depth, lines, budget);
                    }

                    if (!symbolsByFile.TryGetValue(file.Id, out var symbols) || symbols.Count == 0)
                    {
                        continue;
                    }

                    foreach (var symbol in symbols.OrderBy(s => s.StartLine))
                    {
                        var isType = TypeKinds.Contains(symbol.Kind);
                        if (isType && depth < 2 || !isType && depth < 3)
                        {
                            continue;
                        }

                        var text = symbol.Signature ?? symbol.Name;
                        if (!Take(budget, lines, 3, text))
                        {
                            return Finish(repoNames, depth, lines, budget);
                        }
                    }
                }
            }
        }

        if (repoNames.Count == 0)
        {
            throw new WorkspaceException("No repositories in the index. Run 'squeezr index' first.");
        }

        return Finish(repoNames, depth, lines, budget);
    }

    private static RepoMapResult Finish(
        List<string> repoNames, int depth, List<MapLine> lines, TokenBudget budget) =>
        new(repoNames, depth, lines, budget.Used, budget.Truncated, budget.Truncated ? TruncationHint : null);

    private static bool Take(TokenBudget budget, List<MapLine> lines, int depth, string text)
    {
        if (!budget.TryTake(text))
        {
            return false;
        }

        lines.Add(new MapLine(depth, text));
        return true;
    }

    private static string DirName(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "." : path[..index];
    }
}
