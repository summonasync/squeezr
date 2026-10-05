namespace Squeezr.Core.Indexing.Ignore;

// Compiled view of one IgnorePattern: the pattern and its base directory are split once per ruleset,
// so deciding a path costs no allocations beyond the caller's own path split.
internal sealed class IgnoreRule
{
    private readonly IgnorePattern _pattern;
    private readonly string[] _segments;
    private readonly string[]? _base;

    public IgnoreRule(IgnorePattern pattern)
    {
        _pattern = pattern;
        _segments = GlobMatcher.NormalizeGitignorePattern(pattern.CompiledPattern);
        _base = SplitBase(pattern.BaseDir);
    }

    public IgnorePattern Pattern => _pattern;

    public bool Matches(
        string[] rawSegments, string[] loweredSegments, int length, bool isDirectory) =>
        Evaluate(_pattern, _segments, _base, rawSegments, loweredSegments, length, isDirectory);

    public static bool Evaluate(
        IgnorePattern pattern, string[] segments, string[]? baseSegments,
        string[] rawSegments, string[] loweredSegments, int length, bool isDirectory)
    {
        // the ruleset load drops these already; a caller that hands a rule over directly must still get no
        // decision out of a pattern the matcher cannot express
        if (pattern.Unsupported)
            return false;

        if (pattern.DirOnly && !isDirectory)
            return false;

        var offset = 0;
        if (baseSegments is not null)
        {
            if (length < baseSegments.Length)
                return false;

            for (var i = 0; i < baseSegments.Length; i++)
            {
                if (!string.Equals(rawSegments[i], baseSegments[i], StringComparison.Ordinal))
                    return false;
            }

            offset = length == baseSegments.Length ? 0 : baseSegments.Length;
        }

        return pattern.Anchored
            ? GlobMatcher.IsMatch(segments, loweredSegments.AsSpan(offset, length - offset))
            : GlobMatcher.IsMatch(segments, loweredSegments.AsSpan(length - 1, 1));
    }

    public static string[]? SplitBase(string baseDir)
    {
        if (string.IsNullOrEmpty(baseDir) || baseDir == ".")
            return null;

        var trimmed = baseDir.Replace('\\', '/').TrimEnd('/');
        return trimmed.Length == 0 ? [""] : trimmed.Split('/');
    }
}
