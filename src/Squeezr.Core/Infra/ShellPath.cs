namespace Squeezr.Core.Infra;

public static class ShellPath
{
    public static string Quote(string path) => $"'{path.Replace("'", @"'\''")}'";
}
