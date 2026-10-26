using Squeezr.Core.Configuration;

namespace Squeezr.Core.Indexing.Ignore;

public sealed record IgnoreDecision
{
    public required bool Excluded { get; init; }
    public required IgnoreSource? Source { get; init; }
    public required string? Pattern { get; init; }
    public bool TrackedButExcluded { get; init; }
}

public sealed class IgnoreRuleset
{
    private static readonly string[] GitignoreScanPrunedDirs = [".git", "node_modules", "bin", "obj"];
    private static readonly IgnoreDecision Kept = new() { Excluded = false, Source = null, Pattern = null };

    private readonly IgnoreRule[] _rules;
    private readonly GitTrackedSet? _tracked;

    private IgnoreRuleset(List<IgnorePattern> patterns, bool respectsGitignore, GitTrackedSet? tracked)
    {
        Patterns = [.. patterns];
        _rules = [.. patterns.Select(pattern => new IgnoreRule(pattern))];
        RespectsGitignore = respectsGitignore;
        _tracked = tracked;
    }

    public bool RespectsGitignore { get; }

    internal IReadOnlyList<IgnorePattern> Patterns { get; }

    public static IgnoreRuleset For(string repoRoot, SqueezrConfig config, Action<string>? warn = null)
    {
        var patterns = LoadConfiguredPatterns(repoRoot, config, warn);
        if (!config.Indexing.RespectGitignore)
            return new IgnoreRuleset(patterns, false, null);

        CollectGitignorePatterns(repoRoot, repoRoot, patterns, warn);
        return new IgnoreRuleset(patterns, true, GitTrackedSet.Load(repoRoot, warn));
    }

    // A .gitignore only ever applies below its own directory, so for one known path the cascade is the
    // chain of its ancestors: same decisions as For(), without walking the whole tree on every read.
    public static IgnoreRuleset ForFile(
        string repoRoot, SqueezrConfig config, string relativeFilePath, Action<string>? warn = null)
    {
        var patterns = LoadConfiguredPatterns(repoRoot, config, warn);
        if (!config.Indexing.RespectGitignore)
            return new IgnoreRuleset(patterns, false, null);

        CollectGitignoreAncestors(repoRoot, relativeFilePath, patterns, warn);
        return new IgnoreRuleset(patterns, true, GitTrackedSet.Load(repoRoot, warn));
    }

    public IgnoreDecision Decision(string relativePath, bool isDirectory)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var rawSegments = normalizedPath.Split('/');
        var loweredSegments = normalizedPath.ToLowerInvariant().Split('/');

        IgnoreDecision? lastMatch = null;
        bool anyParentExcluded = false;

        foreach (var rule in _rules)
        {
            for (var length = 1; length <= rawSegments.Length; length++)
            {
                var isLast = length == rawSegments.Length;
                var isDir = !isLast || (isLast && isDirectory);

                if (!rule.Matches(rawSegments, loweredSegments, length, isDir))
                    continue;

                if (rule.Pattern.Negate)
                {
                    if (isLast && !anyParentExcluded)
                    {
                        lastMatch = Unexclude(rule);
                    }
                    else if (isDir)
                    {
                        anyParentExcluded = false;
                        lastMatch = Unexclude(rule);
                    }
                }
                else
                {
                    lastMatch = new IgnoreDecision
                    {
                        Excluded = true,
                        Source = rule.Pattern.Source,
                        Pattern = rule.Pattern.Raw
                    };

                    if (isDir && !isLast && rule.Pattern.DirOnly)
                    {
                        anyParentExcluded = true;
                    }
                }
            }
        }

        var decision = lastMatch ?? Kept;
        if (decision.Excluded
            && decision.Source == IgnoreSource.Gitignore
            && _tracked is not null
            && (_tracked.ContainsDirectory(normalizedPath) || _tracked.ContainsFile(normalizedPath)))
        {
            return decision with { Excluded = false, TrackedButExcluded = true };
        }

        return decision;
    }

    private static IgnoreDecision Unexclude(IgnoreRule rule) => new()
    {
        Excluded = false,
        Source = rule.Pattern.Source,
        Pattern = rule.Pattern.Raw
    };

    private static List<IgnorePattern> LoadConfiguredPatterns(
        string repoRoot, SqueezrConfig config, Action<string>? warn)
    {
        var patterns = new List<IgnorePattern>();
        var seenRaw = new HashSet<string>(StringComparer.Ordinal);

        var defaults = SqueezrConfig.CreateDefault().Indexing.Ignore;
        foreach (var pattern in defaults)
        {
            var parsed = GitignoreMatcher.Parse(pattern, IgnoreSource.Defaults, "defaults", ".");
            AddPattern(patterns, parsed, warn);
            if (parsed is not null)
                seenRaw.Add(pattern);
        }

        foreach (var pattern in config.Indexing.Ignore)
        {
            if (seenRaw.Contains(pattern))
                continue;
            AddPattern(patterns, GitignoreMatcher.Parse(pattern, IgnoreSource.Config, "config", "."), warn);
        }

        var squeezrignorePath = Path.Combine(repoRoot, ".squeezrignore");
        if (File.Exists(squeezrignorePath))
        {
            var lines = File.ReadAllLines(squeezrignorePath);
            foreach (var line in lines)
            {
                AddPattern(
                    patterns,
                    GitignoreMatcher.Parse(line, IgnoreSource.Squeezrignore, squeezrignorePath, "."),
                    warn);
            }
        }

        return patterns;
    }

    private static void CollectGitignorePatterns(
        string currentDir, string repoRoot, List<IgnorePattern> patterns, Action<string>? warn)
    {
        AddGitignorePatterns(currentDir, repoRoot, patterns, warn);

        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                var dirName = Path.GetFileName(subDir);
                if (GitignoreScanPrunedDirs.Contains(dirName, StringComparer.Ordinal))
                    continue;

                CollectGitignorePatterns(subDir, repoRoot, patterns, warn);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void CollectGitignoreAncestors(
        string repoRoot, string relativePath, List<IgnorePattern> patterns, Action<string>? warn)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentCount = Math.Max(segments.Length - 1, 0);
        var currentDir = repoRoot;

        for (var i = 0; i <= parentCount; i++)
        {
            AddGitignorePatterns(currentDir, repoRoot, patterns, warn);
            if (i == parentCount || GitignoreScanPrunedDirs.Contains(segments[i], StringComparer.Ordinal))
                return;
            currentDir = Path.Combine(currentDir, segments[i]);
        }
    }

    private static void AddGitignorePatterns(
        string directory, string repoRoot, List<IgnorePattern> patterns, Action<string>? warn)
    {
        var gitignorePath = Path.Combine(directory, ".gitignore");
        if (!File.Exists(gitignorePath))
            return;

        var relativeBase = Path.GetRelativePath(repoRoot, directory).Replace('\\', '/');
        var lines = File.ReadAllLines(gitignorePath);

        foreach (var line in lines)
        {
            AddPattern(
                patterns,
                GitignoreMatcher.Parse(line, IgnoreSource.Gitignore, gitignorePath, relativeBase),
                warn);
        }
    }

    // One policy for every source: a shape the matcher cannot express is never carried, so it can never
    // decide. The sink only chooses whether the author hears about it, never whether the file survives.
    private static void AddPattern(List<IgnorePattern> patterns, IgnorePattern? parsed, Action<string>? warn)
    {
        if (parsed is null)
            return;

        if (parsed.Unsupported)
        {
            if (warn is not null)
                warn(GitignoreMatcher.FormatWarning(parsed.Raw, parsed.SourceFile));
            return;
        }

        patterns.Add(parsed);
    }
}
