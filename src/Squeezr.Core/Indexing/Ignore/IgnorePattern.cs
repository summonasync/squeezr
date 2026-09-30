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
        if (DirOnly && !isDirectory)
            return false;

        var normalizedPath = path.Replace('\\', '/');

        if (!string.IsNullOrEmpty(BaseDir) && BaseDir != ".")
        {
            var baseDirNormalized = BaseDir.Replace('\\', '/').TrimEnd('/');
            if (!normalizedPath.StartsWith(baseDirNormalized + "/", StringComparison.Ordinal) &&
                normalizedPath != baseDirNormalized)
                return false;

            string relativePath;
            if (normalizedPath == baseDirNormalized)
            {
                relativePath = baseDirNormalized;
            }
            else if (normalizedPath.Length == baseDirNormalized.Length + 1)
            {
                relativePath = "";
            }
            else
            {
                relativePath = normalizedPath.Substring(baseDirNormalized.Length + 1);
            }

            var matchPath = Anchored ? relativePath : GetFileName(relativePath);
            return GlobMatcher.IsMatch(CompiledPattern, matchPath);
        }

        var matchPath2 = Anchored ? normalizedPath : GetFileName(normalizedPath);
        return GlobMatcher.IsMatch(CompiledPattern, matchPath2);
    }

    private static string GetFileName(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
    }
}
