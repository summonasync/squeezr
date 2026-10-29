namespace Squeezr.Core.Indexing.Watch;

internal static class ProcStat
{
    internal const char ZombieState = 'Z';

    internal static char? TryReadState(int pid)
    {
        try
        {
            return ParseState(File.ReadAllText($"/proc/{pid}/stat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The comm field between the first '(' and the last ')' may carry parentheses and spaces of its own,
    // so the state is what follows the final ')' — splitting on spaces would misread names like "cat (x)".
    internal static char? ParseState(string statLine)
    {
        var stateIndex = statLine.LastIndexOf(')') + 2;
        return stateIndex > 1 && stateIndex < statLine.Length ? statLine[stateIndex] : null;
    }
}
