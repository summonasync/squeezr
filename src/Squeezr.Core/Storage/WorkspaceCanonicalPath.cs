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

    // Identity guards compare the directory a path reaches, never the spelling that reached it: two link
    // chains naming one store dir are one store, and a guard that reads them as two lets a pass take a
    // lock on a directory it already holds, which fails naming its own pid.
    public static bool SamePhysicalPath(string left, string right) =>
        string.Equals(ResolveThroughLinks(left), ResolveThroughLinks(right), PhysicalComparison);

    // Containment for paths that may not exist yet: the walk above stops at the first missing component
    // and stays lexical from there, so a lane-relative candidate is compared as written.
    public static bool IsInsidePhysicalDirectory(string directory, string candidate)
    {
        var container = ResolveThroughLinks(directory);
        var path = ResolveThroughLinks(candidate);
        if (string.Equals(container, path, PhysicalComparison))
        {
            return true;
        }

        var prefix = container.EndsWith(Path.DirectorySeparatorChar)
            ? container
            : container + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PhysicalComparison);
    }

    private static StringComparison PhysicalComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
