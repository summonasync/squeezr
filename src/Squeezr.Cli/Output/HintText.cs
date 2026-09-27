using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

public static class HintText
{
    private static readonly Dictionary<string, string> CliFlags = new(StringComparer.Ordinal)
    {
        ["repo"] = "--repo",
        ["kind"] = "--kind",
        ["depth"] = "--depth",
        ["maxTokens"] = "--max-tokens",
        ["expand_span"] = "expand <file> <line>"
    };

    public static string ForCli(this TruncationHint hint) => hint.Render(Flag);

    public static string ForMcp(this TruncationHint hint) => hint.Render(static parameter => parameter);

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
