using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public static class WorkspaceStorePaths
{
    // Containment, not charset: WorkspaceStoreKeys.Slug emits non-ASCII letters, so an alphabet check would
    // refuse registry rows the removal path is supposed to handle. ':' is refused on every platform because
    // Windows parses it as an alternate-data-stream separator, putting the real path in front of it ("..:"
    // lands on the parent) where the lexical resolution below cannot see it; Slug never produces a colon.
    public static bool IsSingleSegmentKey(string? storeKey)
    {
        if (string.IsNullOrEmpty(storeKey) || storeKey is "." or ".." || storeKey.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
        {
            return false;
        }

        // The home can be configured with traversal segments or a trailing separator; both sides of the
        // containment compare are normalized so a legal single-segment key is never refused.
        var workspaces = Path.GetFullPath(SqueezrHome.WorkspacesDir);
        var resolved = Path.GetFullPath(Path.Combine(workspaces, storeKey));
        var root = Path.TrimEndingDirectorySeparator(workspaces) + Path.DirectorySeparatorChar;
        return resolved.Length > root.Length
               && resolved.StartsWith(root, StringComparison.Ordinal)
               && resolved.IndexOf(Path.DirectorySeparatorChar, root.Length, resolved.Length - root.Length) < 0;
    }

    public static string RequireStoreDir(string storeKey) =>
        IsSingleSegmentKey(storeKey)
            ? Path.Combine(SqueezrHome.WorkspacesDir, storeKey)
            : throw new WorkspaceException(
                $"Store key '{storeKey}' is not a single path segment: a store key must name one directory " +
                "directly under the workspace store root.");
}
