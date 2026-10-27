using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

public static class HintText
{
    public const string RaiseMaxTokensRemedy = "raise maxTokens";

    private static readonly Dictionary<string, string> CliFlags = new(StringComparer.Ordinal)
    {
        ["repo"] = "--repo",
        ["kind"] = "--kind",
        ["depth"] = "--depth",
        ["maxTokens"] = "--max-tokens",
        ["prefix"] = "--prefix",
        ["expand_span"] = "expand <file> <line>"
    };

    public static string ForCli(this TruncationHint hint) => hint.Render(Flag);

    public static string ForMcp(this TruncationHint hint) => hint.Render(static parameter => parameter);

    public static string TruncationLine(int tokensUsed, int ceiling, string remedy, string? legs = null) =>
        $"truncated: {tokensUsed} of {ceiling} token ceiling — {remedy}"
        + (string.IsNullOrWhiteSpace(legs) ? string.Empty : $" ({legs})");

    public static string? CollapseNote(this SearchMatch match) => match.AlsoMatched switch
    {
        0 => null,
        1 => "also matched 1 more span in this file",
        _ => $"also matched {match.AlsoMatched} more spans in this file"
    };

    public static string ForCli(this WorkspaceException error) => error switch
    {
        ExpandInputException { Issue: ExpandInputIssue.StartLine } =>
            "--start-line must be >= 1.",
        ExpandInputException { Issue: ExpandInputIssue.AmbiguousPath } ambiguous =>
            $"Path '{ambiguous.File}' matches multiple repositories: {string.Join(", ", ambiguous.Candidates)}. Qualify with --repo.",
        _ => error.Message
    };

    private static string Flag(string parameter) =>
        CliFlags.TryGetValue(parameter, out var flag) ? flag : parameter;
}
