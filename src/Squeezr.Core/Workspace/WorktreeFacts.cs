using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Workspace;

// A worktree describes itself in files, not in a queryable API: the toplevel owns a .git marker — a
// directory in the primary worktree, a pointer file in a linked one — and the admin dir that pointer
// names holds HEAD plus a commondir back to the shared git dir. Reading those answers the identity
// questions without spawning git on a query path. The input path's FINAL COMPONENT is resolved, with
// the same helper the registry keys its roots by, so a directory spelled through a link and the
// directory itself give one record and one store name. A link sitting in an ANCESTOR component is not
// resolved here and that spelling survives in Toplevel/StoreName — nobody on this path resolves
// linked ancestors today. Anything the files cannot say is null or unknown: a pointer that no longer
// resolves, a commondir that names a gone directory, a main worktree that is not spelled
// <root>/.git. Half-facts would let a caller register a guess.
public sealed record WorktreeFacts(
    string Toplevel,
    string GitDir,
    string CommonDir,
    string? Branch,
    bool IsDetached,
    bool IsLinked,
    string? MainWorktreeRoot,
    string StoreName)
{
    private const string RefMarker = "ref:";
    private const string HeadsNamespace = "refs/heads/";

    public static WorktreeFacts? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        var toplevel = FindToplevel(WorkspaceCanonicalPath.Canonicalize(path));
        if (toplevel is null)
        {
            return null;
        }

        var marker = Path.Combine(toplevel, ".git");
        if (Directory.Exists(marker))
        {
            return Build(toplevel, marker, marker, false, toplevel);
        }

        var gitDir = GitDirLink.Resolve(toplevel);
        if (gitDir is null)
        {
            return null;
        }

        var commonDir = ReadCommonDir(gitDir);
        if (commonDir is null)
        {
            return null;
        }

        var linked = !SamePath(gitDir, commonDir);
        return Build(toplevel, gitDir, commonDir, linked, linked ? MainRootOf(commonDir) : toplevel);
    }

    private static WorktreeFacts Build(
        string toplevel,
        string gitDir,
        string commonDir,
        bool linked,
        string? mainRoot)
    {
        var (branch, detached) = ReadHead(gitDir);
        return new WorktreeFacts(
            toplevel,
            gitDir,
            commonDir,
            branch,
            detached,
            linked,
            mainRoot,
            StoreNameFor(branch, toplevel, linked));
    }

    private static string? FindToplevel(string path)
    {
        var full = Normalize(path);
        var directory = Directory.Exists(full) ? full : ParentOf(full);
        while (directory is { Length: > 0 })
        {
            if (GitTrackedSet.HasRepoMarker(directory))
            {
                return directory;
            }

            directory = ParentOf(directory);
        }

        return null;
    }

    private static string? ReadCommonDir(string gitDir)
    {
        var pointer = Path.Combine(gitDir, "commondir");
        var value = FirstLine(ReadText(pointer));
        if (value is null)
        {
            return File.Exists(pointer) ? null : gitDir;
        }

        var common = Normalize(Path.IsPathRooted(value) ? value : Path.Combine(gitDir, value));
        return Directory.Exists(common) ? common : null;
    }

    // git keeps the shared dir at <mainRoot>/.git for every worktree it lays out itself; a common dir
    // spelled otherwise (--separate-git-dir) records no main worktree root anywhere. Its linked roots
    // are recorded, at <common>/worktrees/<id>/gitdir, so the null here is about the parent only —
    // there is no file for a later reader to go hunting for.
    private static string? MainRootOf(string commonDir)
    {
        var candidate = ParentOf(commonDir);
        return candidate is not null && SamePath(Path.Combine(candidate, ".git"), commonDir) ? candidate : null;
    }

    private static (string? Branch, bool Detached) ReadHead(string gitDir)
    {
        var head = FirstLine(ReadText(Path.Combine(gitDir, "HEAD")));
        if (head is null)
        {
            return (null, false);
        }

        if (head.StartsWith(RefMarker, StringComparison.Ordinal))
        {
            var target = head[RefMarker.Length..].Trim();
            return target.StartsWith(HeadsNamespace, StringComparison.Ordinal)
                ? (target[HeadsNamespace.Length..], false)
                : (null, false);
        }

        return head.Length is 40 or 64 && head.All(char.IsAsciiHexDigitLower) ? (null, true) : (null, false);
    }

    // Only a linked tree borrows its name from the branch it holds. The primary checkout keeps the
    // folder name, because that is the name `squeezr init` gives it — two spellings for one tree is
    // the identity fork this whole feature exists to close.
    private static string StoreNameFor(string? branch, string toplevel, bool linked)
    {
        var source = linked && branch is not null ? branch : Path.GetFileName(toplevel);
        var slash = source.LastIndexOf('/');
        var leaf = slash >= 0 ? source[(slash + 1)..] : source;
        return WorkspaceStoreKeys.Slug(leaf.Length > 0 ? leaf : source);
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? ParentOf(string path) => Path.GetDirectoryName(Normalize(path));

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? FirstLine(string? text) => text
        ?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
