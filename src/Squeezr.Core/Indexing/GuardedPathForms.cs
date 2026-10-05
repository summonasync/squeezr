using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

// A recorded parameter carries a path in whichever shape the caller had it: the stored repo-relative form, the
// repo-qualified form the surfaces print ('demo/infra/VaultKeys.cs'), the workspace-relative form, or the
// absolute path a tool was handed. The classifier reads the stored form and an anchored extra matches nothing
// else, so a qualified or absolute value would sail through the scrub naming a path the same pass just retired
// from the rows. Every shape the manifest owns folds back to the stored one before the answer is allowed to be
// 'not protected' — the fold only widens the decision, never narrows it.
public sealed class GuardedPathForms(SecretGuardIngestion guard, IReadOnlyList<string> shapes)
{
    public static GuardedPathForms Load(string workspaceRoot, SecretGuardIngestion guard)
    {
        var collected = new List<string> { Trim(workspaceRoot) };
        try
        {
            if (WorkspaceManifestFile.Exists(workspaceRoot))
            {
                foreach (var repo in WorkspaceManifestFile.Load(workspaceRoot).Repos)
                {
                    var repoRoot = NavigationContext.ResolveRepoRoot(workspaceRoot, repo);
                    collected.Add(repo.Name);
                    collected.Add(Trim(repoRoot));
                    collected.Add(Path.GetRelativePath(workspaceRoot, repoRoot).Replace('\\', '/'));
                }
            }
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            // a manifest this pass cannot read is not a licence to trust the raw shapes: the workspace root and
            // the classifier stay in play, the qualified forms simply have nothing to fold against
        }

        return new GuardedPathForms(
            guard,
            [.. collected.Where(shape => shape.Length > 0 && shape != ".")
                .Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    public bool NamesAProtectedPath(string value)
    {
        if (guard.IsProtected(value))
        {
            return true;
        }

        // a leading point segment names the same row as the path behind it, and it breaks every anchored
        // reading the same way a qualified one does — so it comes off before the shapes are folded
        var slashed = value.Replace('\\', '/');
        if (slashed.StartsWith("./", StringComparison.Ordinal))
        {
            slashed = slashed[2..];
            if (guard.IsProtected(slashed))
            {
                return true;
            }
        }

        foreach (var folded in StrippedForms(slashed))
        {
            if (guard.IsProtected(folded))
            {
                return true;
            }
        }

        return false;
    }

    // every shape is a candidate rather than the first one that fits: the same file sits under a repo root and
    // the workspace root at once, and each reading of it gets asked about
    private IEnumerable<string> StrippedForms(string slashed)
    {
        foreach (var shape in shapes)
        {
            if (slashed.Length > shape.Length + 1
                && slashed[shape.Length] == '/'
                && slashed.StartsWith(shape, StringComparison.OrdinalIgnoreCase))
            {
                yield return slashed[(shape.Length + 1)..];
            }
        }
    }

    private static string Trim(string path) => path.Replace('\\', '/').TrimEnd('/');
}
