namespace Squeezr.Core.Indexing.Ignore;

public enum IgnoreSource
{
    Defaults,
    Config,
    Squeezrignore,
    Gitignore
}

public sealed record IgnorePattern
{
    public required string Raw { get; init; }
    public required IgnoreSource Source { get; init; }
    public required string SourceFile { get; init; }
    public required string BaseDir { get; init; }
    public required bool Negate { get; init; }
    public required bool DirOnly { get; init; }
    public required bool Anchored { get; init; }
    public required bool Unsupported { get; init; }
    public required string CompiledPattern { get; init; }

    public bool Matches(string relativePath, bool isDirectory)
    {
        if (Unsupported)
            return false;

        return MatchesPathInternal(relativePath, isDirectory);
    }

    internal bool MatchesPathInternal(string path, bool isDirectory)
    {
        var normalizedPath = path.Replace('\\', '/');
        var rawSegments = normalizedPath.Split('/');
        return IgnoreRule.Evaluate(
            this,
            GlobMatcher.NormalizeGitignorePattern(CompiledPattern),
            IgnoreRule.SplitBase(BaseDir),
            rawSegments,
            normalizedPath.ToLowerInvariant().Split('/'),
            rawSegments.Length,
            isDirectory);
    }
}
