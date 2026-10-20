using System.Text.Json.Serialization;
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
    TruncationHint? Hint,
    string? ScopedPrefix = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? GuardWarnings = null);

public sealed class RepoMapper
{
    public const int DefaultDepth = 2;

    public const int DefaultMaxTokens = 4000;

    private static readonly TruncationHint Truncation = new(
    [
        new("lower", ["depth"]),
        new("raise", ["maxTokens"])
    ]);

    private readonly string _workspaceRoot;

    public RepoMapper(string workspaceRoot) => _workspaceRoot = NavigationContext.RequireIndex(workspaceRoot);

    public RepoMapResult Build(string? repo, int depth, int maxTokens, string? prefix = null)
    {
        if (depth < 1)
        {
            throw new WorkspaceException("depth must be >= 1.");
        }

        var normalizedPrefix = PathPrefix.Normalize(prefix);
        var manifest = WorkspaceManifestFile.Load(_workspaceRoot);
        var guard = SecretGuardReader.Load(_workspaceRoot);
        // a prefix that itself names a secret is a request about that secret: answering "no such subtree"
        // would be the absence oracle the guard exists to close
        if (normalizedPrefix is not null && guard.IsProtected(normalizedPrefix))
        {
            throw guard.Refusal(normalizedPrefix);
        }

        var budget = new TokenBudget(maxTokens);
        var lines = new List<MapLine>();
        var repoNames = new List<string>();
        List<string>? unscopedPaths = normalizedPrefix is null ? null : [];
        string? scopedPrefix = null;

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot));
        foreach (var entry in NavigationContext.SelectRepos(manifest, repo))
        {
            var repoDoc = store.FindRepo(entry.Name);
            if (repoDoc is null)
            {
                continue;
            }

            var stored = store.ListFiles(repoDoc.Id);
            IReadOnlyList<FileDocument> files;
            if (normalizedPrefix is not null)
            {
                // existence is decided before the guard, exactly like SearchEngine.ResolveFileScope: a
                // subtree that exists but holds only secrets maps empty with a withheld count, and only a
                // prefix matching nothing at all is the "no such subtree" answer
                var matching = stored.Where(f => PathPrefix.Matches(normalizedPrefix, f.Path)).ToList();
                if (matching.Count == 0)
                {
                    unscopedPaths!.AddRange(stored.Where(f => !guard.IsProtected(f.Path)).Select(f => f.Path));
                    continue;
                }

                // matching is case-insensitive but stored paths are not — keep the stored
                // casing so downstream path lookups (freshness guard) stay exact
                scopedPrefix ??= matching[0].Path[..normalizedPrefix.Length];
                files = Serve(matching, guard);
            }
            else
            {
                files = Serve(stored, guard);
            }

            repoNames.Add(repoDoc.Name);
            if (!Take(budget, lines, 0, $"{repoDoc.Name}/"))
            {
                return Finish(repoNames, depth, lines, budget, scopedPrefix, guard.Warnings);
            }

            var symbolsByFile = store.ListSymbolsByRepo(repoDoc.Id)
                .GroupBy(s => s.FileId)
                .ToDictionary(g => g.Key, g => g.ToList());

            string SubtreeRelative(string path) =>
                normalizedPrefix is null ? path : PathPrefix.Strip(normalizedPrefix, path);

            foreach (var dir in files.GroupBy(f => DirName(SubtreeRelative(f.Path))).OrderBy(g => g.Key))
            {
                if (!Take(budget, lines, 1, $"{dir.Key}/ ({dir.Count()} files)"))
                {
                    return Finish(repoNames, depth, lines, budget, scopedPrefix, guard.Warnings);
                }

                if (depth < 2)
                {
                    continue;
                }

                foreach (var file in dir.OrderBy(f => f.Path))
                {
                    if (!Take(budget, lines, 2, Path.GetFileName(file.Path)))
                    {
                        return Finish(repoNames, depth, lines, budget, scopedPrefix, guard.Warnings);
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
                            return Finish(repoNames, depth, lines, budget, scopedPrefix, guard.Warnings);
                        }
                    }
                }
            }
        }

        if (normalizedPrefix is not null && repoNames.Count == 0 && unscopedPaths!.Count > 0)
        {
            throw PathPrefix.NoSubtreeException(normalizedPrefix, unscopedPaths);
        }

        if (repoNames.Count == 0)
        {
            throw new WorkspaceException("No repositories in the index. Run 'squeezr index' first.");
        }

        return Finish(repoNames, depth, lines, budget, scopedPrefix, guard.Warnings);
    }

    private static List<FileDocument> Serve(IEnumerable<FileDocument> files, SecretGuardReader guard)
    {
        var served = new List<FileDocument>();
        foreach (var file in files)
        {
            if (!guard.TryWithhold(file.Path))
            {
                served.Add(file);
            }
        }

        return served;
    }

    private static RepoMapResult Finish(
        List<string> repoNames,
        int depth,
        List<MapLine> lines,
        TokenBudget budget,
        string? scopedPrefix,
        IReadOnlyList<string>? guardWarnings) =>
        new(repoNames, depth, lines, budget.Used, budget.Truncated, budget.Truncated ? Truncation : null, scopedPrefix, guardWarnings);

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
