using Squeezr.Core.Storage;

namespace Squeezr.Lite.Mcp;

public static class SessionLocation
{
    public static bool IsWithinRoot(string workspaceRoot, string sessionDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Display(workspaceRoot);
        var session = Display(sessionDirectory);

        return string.Equals(session, root, comparison)
            || session.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    public static string Display(string path) =>
        WorkspaceCanonicalPath.Canonicalize(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
