namespace Squeezr.Core.Indexing;

public static class GlobMatcher
{
    public static bool IsMatch(string pattern, string path) =>
        MatchSegments(NormalizePattern(pattern), 0, path.ToLowerInvariant().Split('/'), 0);

    internal static string[] NormalizePattern(string pattern) =>
        pattern.Replace('\\', '/').ToLowerInvariant().Split('/');

    internal static bool IsMatch(string[] normalizedPattern, ReadOnlySpan<string> pathSegments) =>
        MatchSegments(normalizedPattern, 0, pathSegments, 0);

    private static bool MatchSegments(string[] pattern, int patternIndex, ReadOnlySpan<string> segments, int segmentIndex)
    {
        if (patternIndex == pattern.Length)
        {
            return segmentIndex == segments.Length;
        }

        if (pattern[patternIndex] == "**")
        {
            for (var skip = segmentIndex; skip <= segments.Length; skip++)
            {
                if (MatchSegments(pattern, patternIndex + 1, segments, skip))
                {
                    return true;
                }
            }

            return false;
        }

        if (segmentIndex == segments.Length)
        {
            return false;
        }

        return MatchSegment(pattern[patternIndex], segments[segmentIndex])
               && MatchSegments(pattern, patternIndex + 1, segments, segmentIndex + 1);
    }

    private static bool MatchSegment(string pattern, string segment)
    {
        var p = 0;
        var s = 0;
        var starPattern = -1;
        var starSegment = -1;

        while (s < segment.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == segment[s]))
            {
                p++;
                s++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPattern = p++;
                starSegment = s;
            }
            else if (starPattern >= 0)
            {
                p = starPattern + 1;
                s = ++starSegment;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
