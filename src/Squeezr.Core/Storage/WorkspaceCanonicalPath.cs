namespace Squeezr.Core.Storage;

public static class WorkspaceCanonicalPath
{
    public static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full);
        if (trimmed.Length == 0
            || (root is { Length: > 0 }
                && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal)))
        {
            trimmed = root ?? full;
        }

        if (!Directory.Exists(trimmed))
        {
            return trimmed;
        }

        var target = new DirectoryInfo(trimmed).ResolveLinkTarget(returnFinalTarget: true);
        return target?.FullName ?? trimmed;
    }
}
