namespace Squeezr.Core.Indexing.Navigation;

public sealed record HintSuggestion(string Verb, IReadOnlyList<string> Parameters);

public sealed record TruncationHint(IReadOnlyList<HintSuggestion> Suggestions)
{
    public string Render(Func<string, string> parameterName) =>
        string.Join(" or ", Suggestions.Select(s => Clause(s, parameterName)));

    private static string Clause(HintSuggestion suggestion, Func<string, string> parameterName)
    {
        var names = suggestion.Parameters.Select(parameterName).ToList();
        return names.Count == 1
            ? $"{suggestion.Verb} {names[0]}"
            : $"{suggestion.Verb} ({string.Join(", ", names)})";
    }
}
