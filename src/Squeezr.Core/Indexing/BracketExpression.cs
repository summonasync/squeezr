namespace Squeezr.Core.Indexing;

// Bracket expressions in git glob syntax: '[' , optional '!' or '^' negation, then members where a
// leading ']' is a member, '-' first or last is a member, 'x-y' is a range and '\X' escapes X.
// An expression that never closes is not a bracket: git aborts the match, so callers get no extent.
internal static class BracketExpression
{
    public const int NoExtent = -1;

    // Callers that only need the extent of an expression (validators, segment splitters) match it against
    // a character no path can hold, so the boolean result carries no meaning for them.
    public const char StructuralProbe = '\u0000';

    public static bool Matches(string pattern, int start, char value, out int endIndex)
    {
        endIndex = NoExtent;

        var i = start + 1;
        if (i >= pattern.Length)
            return false;

        var negate = pattern[i] == '!' || pattern[i] == '^';
        if (negate)
            i++;

        var matched = false;
        var members = 0;

        while (i < pattern.Length)
        {
            if (pattern[i] == ']' && members > 0)
            {
                endIndex = i + 1;
                return matched != negate;
            }

            ReadMember(pattern, ref i, out var low);

            var isRange = i + 1 < pattern.Length && pattern[i] == '-' && pattern[i + 1] != ']';
            if (isRange)
            {
                i++;
                ReadMember(pattern, ref i, out var high);
                // A reversed range is not an empty set: git matches the start character alone
                // (probed against git 2.53: [z-a] ignores "z", keeps "a", "-", "{").
                matched |= high >= low ? value >= low && value <= high : value == low;
            }
            else
            {
                matched |= value == low;
            }

            members++;
        }

        return false;
    }

    private static void ReadMember(string pattern, ref int index, out char member)
    {
        member = pattern[index];
        index++;

        if (member != '\\' || index >= pattern.Length)
            return;

        member = pattern[index];
        index++;
    }
}
