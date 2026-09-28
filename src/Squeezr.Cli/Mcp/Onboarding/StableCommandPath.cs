namespace Squeezr.Cli.Mcp.Onboarding;

public static class StableCommandPath
{
    private const string RelativeLinkPath = ".local/bin/squeezr";

    public static string Resolve(string homeDir, string? processPath)
    {
        var link = Path.Combine(homeDir, RelativeLinkPath);
        if (IsUsablePath(link))
        {
            return link;
        }

        if (processPath is null)
        {
            throw new InvalidOperationException("Cannot resolve the path of the current executable.");
        }

        return processPath;
    }

    private static bool IsUsablePath(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            var resolved = Path.IsPathRooted(info.LinkTarget)
                ? info.LinkTarget
                : Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(path)!);
            return File.Exists(resolved);
        }
        return info.Exists;
    }
}
