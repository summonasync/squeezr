using Squeezr.Cli.Commands;

namespace Squeezr.Cli.Mcp;

internal static class SymbolParamAlias
{
    public static string Resolve(string tool, string? canonical, string? alias, string canonicalName, string aliasName)
    {
        if (canonical is not null && alias is not null
            && !string.Equals(canonical, alias, StringComparison.Ordinal))
        {
            throw new McpUsageException(
                $"{tool} accepts one symbol argument: '{canonicalName}' and its alias '{aliasName}' were both "
                + $"passed with different values ('{canonical}' vs '{alias}') — pass one of them.");
        }

        if (string.IsNullOrWhiteSpace(canonical ?? alias))
        {
            throw new McpUsageException(
                $"no symbol given — {tool} accepts '{canonicalName}' or its alias '{aliasName}', "
                + $"e.g. {{ \"{canonicalName}\": \"OrderService\" }}");
        }

        return canonical ?? alias!;
    }
}
