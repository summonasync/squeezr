using Squeezr.Core.Configuration;

namespace Squeezr.Core.Indexing.Ignore;

public sealed record IgnoreDecision
{
    public required bool Excluded { get; init; }
    public required IgnoreSource? Source { get; init; }
    public required string? Pattern { get; init; }
}

public sealed class IgnoreRuleset
{
    private static readonly string[] GitignoreScanPrunedDirs = [".git", "node_modules", "bin", "obj"];

    private readonly List<IgnorePattern> _patterns;

    private IgnoreRuleset(List<IgnorePattern> patterns, bool respectsGitignore)
    {
        _patterns = patterns;
        RespectsGitignore = respectsGitignore;
    }

    public bool RespectsGitignore { get; }

    public static IgnoreRuleset For(string repoRoot, SqueezrConfig config, Action<string>? warn = null)
    {
        var patterns = LoadConfiguredPatterns(repoRoot, config, warn);
        if (config.Indexing.RespectGitignore)
            CollectGitignorePatterns(repoRoot, repoRoot, patterns, warn);

        return new IgnoreRuleset(patterns, config.Indexing.RespectGitignore);
    }

    // A .gitignore only ever applies below its own directory, so for one known path the cascade is the
    // chain of its ancestors: same decisions as For(), without walking the whole tree on every read.
    public static IgnoreRuleset ForFile(
        string repoRoot, SqueezrConfig config, string relativeFilePath, Action<string>? warn = null)
    {
        var patterns = LoadConfiguredPatterns(repoRoot, config, warn);
        if (config.Indexing.RespectGitignore)
            CollectGitignoreAncestors(repoRoot, relativeFilePath, patterns, warn);

        return new IgnoreRuleset(patterns, config.Indexing.RespectGitignore);
    }

    private static List<IgnorePattern> LoadConfiguredPatterns(
        string repoRoot, SqueezrConfig config, Action<string>? warn)
    {
        var patterns = new List<IgnorePattern>();
        var seenRaw = new HashSet<string>(StringComparer.Ordinal);

        var defaults = SqueezrConfig.CreateDefault().Indexing.Ignore;
        foreach (var pattern in defaults)
        {
            var parsed = GitignoreMatcher.Parse(pattern, IgnoreSource.Defaults, "defaults", ".");
            if (parsed is not null)
            {
                patterns.Add(parsed);
                seenRaw.Add(pattern);
            }
        }

        foreach (var pattern in config.Indexing.Ignore)
        {
            if (seenRaw.Contains(pattern))
                continue;
            var parsed = GitignoreMatcher.Parse(pattern, IgnoreSource.Config, "config", ".");
            if (parsed is not null)
                patterns.Add(parsed);
        }

        var squeezrignorePath = Path.Combine(repoRoot, ".squeezrignore");
        if (File.Exists(squeezrignorePath))
        {
            var lines = File.ReadAllLines(squeezrignorePath);
            foreach (var line in lines)
            {
                var parsed = GitignoreMatcher.Parse(line, IgnoreSource.Squeezrignore, squeezrignorePath, ".");
                if (parsed is not null)
                {
                    if (parsed.Unsupported && warn is not null)
                        warn(GitignoreMatcher.FormatWarning(parsed.Raw, squeezrignorePath));
                    else
                        patterns.Add(parsed);
                }
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
            var parsed = GitignoreMatcher.Parse(line, IgnoreSource.Gitignore, gitignorePath, relativeBase);
            if (parsed is not null)
            {
                if (parsed.Unsupported && warn is not null)
                    warn(GitignoreMatcher.FormatWarning(parsed.Raw, gitignorePath));
                else
                    patterns.Add(parsed);
            }
        }
    }

    public IgnoreDecision Decision(string relativePath, bool isDirectory)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var segments = normalizedPath.Split('/');

        IgnoreDecision? lastMatch = null;
        bool anyParentExcluded = false;

        foreach (var pattern in _patterns)
        {
            for (var i = 1; i <= segments.Length; i++)
            {
                var isLast = i == segments.Length;
                var isDir = !isLast || (isLast && isDirectory);
                var path = string.Join('/', segments.Take(i));

                if (!pattern.MatchesPathInternal(path, isDir))
                    continue;

                if (pattern.Negate)
                {
                    if (isLast && !anyParentExcluded)
                    {
                        lastMatch = new IgnoreDecision
                        {
                            Excluded = false,
                            Source = pattern.Source,
                            Pattern = pattern.Raw
                        };
                    }
                    else if (isDir)
                    {
                        anyParentExcluded = false;
                        lastMatch = new IgnoreDecision
                        {
                            Excluded = false,
                            Source = pattern.Source,
                            Pattern = pattern.Raw
                        };
                    }
                }
                else
                {
                    lastMatch = new IgnoreDecision
                    {
                        Excluded = true,
                        Source = pattern.Source,
                        Pattern = pattern.Raw
                    };

                    if (isDir && !isLast && pattern.DirOnly)
                    {
                        anyParentExcluded = true;
                    }
                }
            }
        }

        return lastMatch ?? new IgnoreDecision
        {
            Excluded = false,
            Source = null,
            Pattern = null
        };
    }
}
