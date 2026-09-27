using Squeezr.Core.Indexing.Navigation;

namespace Squeezr.Cli.Output;

public static class HintText
{
    private static readonly Dictionary<string, string> CliFlags = new(StringComparer.Ordinal)
    {
        ["repo"] = "--repo",
        ["kind"] = "--kind",
        ["depth"] = "--depth",
        ["maxTokens"] = "--max-tokens"
    };

    public static string ForCli(this TruncationHint hint) => hint.Render(Flag);

    public static string ForMcp(this TruncationHint hint) => hint.Render(static parameter => parameter);

    private static string Flag(string parameter) =>
        CliFlags.TryGetValue(parameter, out var flag) ? flag : parameter;
}
