namespace Squeezr.Cli.Mcp;

internal static class ShellPath
{
    public static string Quote(string path) => $"'{path.Replace("'", @"'\''")}'";
}
