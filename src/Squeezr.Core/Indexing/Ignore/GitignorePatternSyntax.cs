namespace Squeezr.Core.Indexing.Ignore;

// Residual gitignore shapes the matcher cannot express: a bracket expression that never closes, a
// POSIX [:class:] name (git resolves these, we skip the pattern instead of guessing a set), an
// escaped path separator, and a backslash with nothing to escape.
internal static class GitignorePatternSyntax
{
    public static bool IsExpressible(string pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '\\':
                    if (i + 1 >= pattern.Length || pattern[i + 1] == '/')
                        return false;
                    i++;
                    break;

                case '[':
                    BracketExpression.Matches(pattern, i, BracketExpression.StructuralProbe, out var end);
                    if (end == BracketExpression.NoExtent)
                        return false;

                    if (ContainsPosixClass(pattern, i, end))
                        return false;

                    i = end - 1;
                    break;
            }
        }

        return true;
    }

    private static bool ContainsPosixClass(string pattern, int from, int until)
    {
        for (var i = from; i + 3 < until; i++)
        {
            if (pattern[i] != '[' || pattern[i + 1] != ':')
                continue;

            var name = i + 2;
            var at = name;
            while (at < until && char.IsLetter(pattern[at]))
                at++;

            if (at > name && at + 1 < until && pattern[at] == ':' && pattern[at + 1] == ']')
                return true;
        }

        return false;
    }
}
