using System.Text;

namespace Squeezr.Core.Indexing.Query;

public static class RegexQueryAnalyzer
{
    public static List<string[]> ExtractTrigramGroups(string pattern)
    {
        var groups = new List<string[]>();
        var segments = SplitTopLevelAlternation(pattern);
        if (segments.Count > 1)
        {
            var union = new List<string>();
            var allPure = true;
            foreach (var segment in segments)
            {
                var segmentGroups = new List<string[]>();
                var pure = ParseRun(segment, segmentGroups);
                if (!pure)
                {
                    allPure = false;
                    break;
                }

                foreach (var g in segmentGroups)
                    union.AddRange(g);
            }

            if (allPure && union.Count > 0)
                groups.Add(union.ToArray());

            return groups;
        }

        ParseRun(pattern, groups);
        return groups;
    }

    private static List<string> SplitTopLevelAlternation(string pattern)
    {
        var segments = new List<string>();
        var depth = 0;
        var inClass = false;
        var start = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\') { i++; continue; }
            if (inClass) { if (c == ']') inClass = false; continue; }
            if (c == '[') { inClass = true; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (c == '|' && depth == 0)
            {
                segments.Add(pattern[start..i]);
                start = i + 1;
            }
        }

        if (segments.Count == 0)
            segments.Add(pattern);
        else
            segments.Add(pattern[start..]);

        return segments;
    }

    private static bool ParseRun(string pattern, List<string[]> groups)
    {
        var pure = true;
        var run = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < pattern.Length)
                    {
                        var e = pattern[++i];
                        if (e is 'n' or 't' or 'r') run.Append(e == 'n' ? '\n' : e == 't' ? '\t' : '\r');
                        else if (char.IsPunctuation(e) || e == ' ') run.Append(e);
                        else { FlushRun(run, groups); pure = false; }
                    }
                    break;
                case '.':
                    FlushRun(run, groups);
                    pure = false;
                    break;
                case '[':
                    {
                        FlushRun(run, groups);
                        pure = false;
                        var j = i + 1;
                        if (j < pattern.Length && pattern[j] == '^') j++;
                        if (j < pattern.Length && pattern[j] == ']') j++;
                        while (j < pattern.Length && pattern[j] != ']') j++;
                        i = j;
                        break;
                    }
                case '*':
                case '?':
                    if (run.Length > 0) run.Length--;
                    FlushRun(run, groups);
                    pure = false;
                    break;
                case '+':
                    FlushRun(run, groups);
                    pure = false;
                    break;
                case '{':
                    {
                        var j = pattern.IndexOf('}', i);
                        var allowsZero = j > i + 1 && pattern[i + 1] == '0';
                        if (allowsZero && run.Length > 0) run.Length--;
                        FlushRun(run, groups);
                        pure = false;
                        if (j >= 0) i = j;
                        break;
                    }
                case '(':
                    {
                        FlushRun(run, groups);
                        var j = FindClosingParen(pattern, i);
                        if (j < 0) { pure = false; i = pattern.Length - 1; break; }
                        var inner = pattern[(i + 1)..j];
                        var optional = j + 1 < pattern.Length && (pattern[j + 1] is '*' or '?');
                        if (!optional)
                        {
                            var innerGroups = ExtractTrigramGroups(inner);
                            groups.AddRange(innerGroups);
                        }
                        pure = false;
                        i = j;
                        break;
                    }
                case ')':
                    pure = false;
                    break;
                default:
                    run.Append(c);
                    break;
            }
        }

        FlushRun(run, groups);
        return pure;
    }

    private static void FlushRun(StringBuilder run, List<string[]> groups)
    {
        for (var t = 0; t + 3 <= run.Length; t++)
            groups.Add(new[] { run.ToString(t, 3) });

        run.Clear();
    }

    private static int FindClosingParen(string pattern, int open)
    {
        var depth = 0;
        for (var i = open; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\') { i++; continue; }
            if (pattern[i] == '(') depth++;
            else if (pattern[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }
}
