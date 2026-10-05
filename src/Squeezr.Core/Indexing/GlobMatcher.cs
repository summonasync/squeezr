namespace Squeezr.Core.Indexing;

public static class GlobMatcher
{
    public static bool IsMatch(string pattern, string path) =>
        MatchSegments(NormalizePattern(pattern), 0, path.ToLowerInvariant().Split('/'), 0);

    internal static string[] NormalizePattern(string pattern) =>
        pattern.Replace('\\', '/').ToLowerInvariant().Split('/');

    // The ignore dialect keeps '\' as the escape character, so it cannot share the normalization above:
    // 'Generated\ Files' is one segment there, two segments here. Slashes inside a bracket expression
    // are members, not separators — '[a/]x' is one segment, and git matches it against 'ax'.
    internal static string[] NormalizeGitignorePattern(string pattern)
    {
        var segments = new List<string>();
        var start = 0;

        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    if (i + 1 < pattern.Length)
                        i++;
                    break;

                case '[':
                    BracketExpression.Matches(pattern, i, BracketExpression.StructuralProbe, out var end);
                    if (end != BracketExpression.NoExtent)
                        i = end - 1;
                    break;

                case '/':
                    segments.Add(pattern[start..i].ToLowerInvariant());
                    start = i + 1;
                    break;
            }
        }

        segments.Add(pattern[start..].ToLowerInvariant());
        return [.. segments];
    }

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
            if (p < pattern.Length && pattern[p] is '[' or '\\')
            {
                var outcome = MatchUnit(pattern, p, segment[s], out var unitEnd);
                if (outcome != UnitOutcome.Malformed)
                {
                    if (outcome == UnitOutcome.Match)
                    {
                        p = unitEnd;
                        s++;
                        continue;
                    }

                    if (starPattern < 0)
                    {
                        return false;
                    }

                    p = starPattern + 1;
                    s = ++starSegment;
                    continue;
                }
            }

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

    private static UnitOutcome MatchUnit(string pattern, int index, char value, out int unitEnd)
    {
        if (pattern[index] == '[')
        {
            var matched = BracketExpression.Matches(pattern, index, value, out unitEnd);
            if (unitEnd == BracketExpression.NoExtent)
            {
                return UnitOutcome.Malformed;
            }

            return matched ? UnitOutcome.Match : UnitOutcome.Miss;
        }

        if (index + 1 == pattern.Length)
        {
            unitEnd = index;
            return UnitOutcome.Malformed;
        }

        unitEnd = index + 2;
        return pattern[index + 1] == value ? UnitOutcome.Match : UnitOutcome.Miss;
    }

    private enum UnitOutcome
    {
        Match,
        Miss,
        Malformed
    }
}
