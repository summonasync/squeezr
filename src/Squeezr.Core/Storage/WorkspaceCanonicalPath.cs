namespace Squeezr.Core.Storage;

public static class WorkspaceCanonicalPath
{
    public static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full);
        var isFilesystemRoot = trimmed.Length == 0
            || (root is { Length: > 0 }
                && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal));
        if (isFilesystemRoot)
        {
            // A volume root, a UNC share root and "/" can never be links, and on
            // Windows ResolveLinkTarget throws DirectoryNotFoundException for one.
            return root ?? full;
        }

        if (!Directory.Exists(trimmed))
        {
            return trimmed;
        }

        var target = new DirectoryInfo(trimmed).ResolveLinkTarget(returnFinalTarget: true);
        return target?.FullName ?? trimmed;
    }

    // ResolveLinkTarget answers only for the final component, so a directory reached through a linked
    // ancestor (<d>/link/.squeezr naming <d>/real/.squeezr) stays textually different from the spelling
    // a delete path resolves to. Identity guards need the directory, not the spelling. A component that
    // does not exist ends the walk and stays lexical, so an unreadable path never throws.
    public static string ResolveThroughLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not { Length: > 0 })
        {
            return full;
        }

        var segments = full[root.Length..]
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            var candidate = Path.Combine(current, segments[index]);
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                var rest = string.Join(Path.DirectorySeparatorChar, segments, index, segments.Length - index);
                return Path.GetFullPath(Path.Combine(current, rest));
            }

            current = FinalLinkTargetOf(candidate) ?? candidate;
        }

        return current;
    }

    private static string? FinalLinkTargetOf(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                : new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
