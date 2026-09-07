using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Navigation;

public sealed record ExpandResult(
    string Repo,
    string File,
    int StartLine,
    int EndLine,
    int Context,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> Span,
    IReadOnlyList<string> After,
    int TokensUsed);

public sealed class SpanExpander
{
    public const int DefaultContextLines = 3;

    public const int DefaultMaxTokens = 1000;

    private readonly string _workspaceRoot;

    public SpanExpander(string workspaceRoot) => _workspaceRoot = NavigationContext.RequireIndex(workspaceRoot);

    public ExpandResult Expand(string file, int startLine, int? endLine, int contextLines, string? repo, int maxTokens)
    {
        if (startLine < 1)
        {
            throw new WorkspaceException("--start-line must be >= 1.");
        }

        var manifest = WorkspaceManifestFile.Load(_workspaceRoot);
        var (entry, fileDoc) = ResolveFile(manifest, file, repo);

        var fullPath = Path.Combine(
            NavigationContext.ResolveRepoRoot(_workspaceRoot, entry),
            fileDoc.Path.Replace('/', Path.DirectorySeparatorChar));
        string[] lines;
        try
        {
            lines = File.ReadAllLines(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceException($"File '{fileDoc.Path}' is unreadable: {ex.Message}");
        }

        if (lines.Length == 0)
        {
            throw new WorkspaceException($"File '{fileDoc.Path}' is empty.");
        }

        var start = Math.Clamp(startLine, 1, lines.Length);
        var end = Math.Clamp(endLine ?? startLine, start, lines.Length);
        var context = Math.Max(0, contextLines);

        var beforeStart = Math.Max(1, start - context);
        var before = lines[(beforeStart - 1)..(start - 1)];
        var span = lines[(start - 1)..end];
        var afterEnd = Math.Min(lines.Length, end + context);
        var after = lines[end..afterEnd];

        var budget = new TokenBudget(maxTokens);
        foreach (var line in span)
        {
            budget.TryTake(line);
        }

        foreach (var line in before)
        {
            budget.TryTake(line);
        }

        foreach (var line in after)
        {
            budget.TryTake(line);
        }

        return new ExpandResult(entry.Name, fileDoc.Path, start, end, context, before, span, after, budget.Used);
    }

    private (RepoEntry Entry, FileDocument File) ResolveFile(WorkspaceManifest manifest, string file, string? repo)
    {
        var normalized = file.Replace('\\', '/').TrimStart('/');
        var direct = TryResolve(manifest, normalized, repo);

        var slash = normalized.IndexOf('/');
        var prefixRepo = slash > 0 && manifest.Repos.Any(r => string.Equals(r.Name, normalized[..slash], StringComparison.OrdinalIgnoreCase))
            ? normalized[..slash]
            : null;

        (RepoEntry Entry, FileDocument File)? prefixed = null;
        if (prefixRepo is not null
            && (repo is null || string.Equals(prefixRepo, repo, StringComparison.OrdinalIgnoreCase)))
        {
            prefixed = TryResolve(manifest, normalized[(slash + 1)..], prefixRepo);
        }

        if (direct is not null && prefixed is not null
            && (!string.Equals(direct.Value.Entry.Name, prefixed.Value.Entry.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(direct.Value.File.Path, prefixed.Value.File.Path, StringComparison.Ordinal)))
        {
            throw AmbiguousPath(
                file,
                [$"{direct.Value.Entry.Name}:{direct.Value.File.Path}", $"{prefixed.Value.Entry.Name}:{prefixed.Value.File.Path}"]);
        }

        var resolved = direct ?? prefixed;
        if (resolved is null)
        {
            throw new WorkspaceException($"File '{file}' is not in the index. Run 'squeezr index' if the code changed.");
        }

        return resolved.Value;
    }

    private (RepoEntry Entry, FileDocument File)? TryResolve(WorkspaceManifest manifest, string normalized, string? repo)
    {
        var candidates = new List<(RepoEntry Entry, FileDocument File)>();
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot));
        foreach (var entry in NavigationContext.SelectRepos(manifest, repo))
        {
            var repoDoc = store.FindRepo(entry.Name);
            if (repoDoc is null)
            {
                continue;
            }

            var files = store.ListFiles(repoDoc.Id);
            var exact = files.FirstOrDefault(f => f.Path.Equals(normalized, PathComparison));
            if (exact is not null)
            {
                candidates.Add((entry, exact));
                continue;
            }

            var suffix = files.Where(f => f.Path.EndsWith('/' + normalized, PathComparison)).ToList();
            if (suffix.Count > 1)
            {
                throw AmbiguousPath(normalized, suffix.Select(f => $"{entry.Name}:{f.Path}"));
            }

            if (suffix.Count == 1)
            {
                candidates.Add((entry, suffix[0]));
            }
        }

        if (candidates.Count > 1)
        {
            throw AmbiguousPath(normalized, candidates.Select(c => $"{c.Entry.Name}:{c.File.Path}"));
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static WorkspaceException AmbiguousPath(string file, IEnumerable<string> candidates) =>
        new($"Path '{file}' matches multiple repositories: {string.Join(", ", candidates)}. Qualify with --repo.");

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
