using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

// A recorded parameter carries a path in whichever shape the caller had it: the stored repo-relative form, the
// repo-qualified form the surfaces print ('demo/infra/VaultKeys.cs'), the workspace-relative form, or the
// absolute path a tool was handed. The classifier reads the stored form and an anchored extra matches nothing
// else, so a qualified or absolute value would sail through the scrub naming a path the same pass just retired
// from the rows. Every shape the manifest owns folds back to the stored one before the answer is allowed to be
// 'not protected' — the fold only widens the decision, never narrows it.
// Three surfaces ask this question about paths that never looked alike: the store-side scrub rewrites a value the
// ledger recorded, stats drops a row before doing arithmetic with it, and the read hook answers before it proves a
// file exists. One class answers all three — a mask that protects a shape on one surface and not on another is the
// same blind spot three times over, and the answer the hook gives is an existence oracle.
public sealed class GuardedPathForms
{
    private readonly Func<string, bool> _classify;
    private readonly IReadOnlyList<string> _shapes;
    private readonly IReadOnlyList<string> _roots;

    // contract of use: one instance belongs to one pass, on one thread — every caller Loads its own. The memo is
    // unsynchronized by choice, so an instance cached and shared across threads (an MCP session, a watch daemon)
    // would race on it; shapes, roots and the classifier are frozen at Load, which is what makes it cacheable.
    private readonly Dictionary<string, bool> _folded = new(StringComparer.Ordinal);

    private GuardedPathForms(
        Func<string, bool> classify, IReadOnlyList<string> shapes, IReadOnlyList<string> roots)
    {
        _classify = classify;
        _shapes = shapes;
        _roots = roots;
    }

    // the write plane asks the ingestion classifier, the read planes ask the reader; the fold is the same work
    // either way, and so is the answer for a value no shape can be read from it — that one is owned below
    public static GuardedPathForms Load(string workspaceRoot, SecretGuardIngestion guard) =>
        Load(workspaceRoot, guard.IsProtected);

    public static GuardedPathForms Load(string workspaceRoot, SecretGuardReader guard) =>
        Load(workspaceRoot, guard.IsProtected);

    private static GuardedPathForms Load(string workspaceRoot, Func<string, bool> classify)
    {
        var shapes = new List<string> { Trim(workspaceRoot) };
        var roots = new List<string> { Trim(workspaceRoot) };
        try
        {
            if (WorkspaceManifestFile.Exists(workspaceRoot))
            {
                foreach (var repo in WorkspaceManifestFile.Load(workspaceRoot).Repos)
                {
                    var repoRoot = Trim(NavigationContext.ResolveRepoRoot(workspaceRoot, repo));
                    shapes.Add(repo.Name);
                    shapes.Add(repoRoot);
                    shapes.Add(Path.GetRelativePath(workspaceRoot, repoRoot).Replace('\\', '/'));
                    roots.Add(repoRoot);
                }
            }
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            // a manifest this pass cannot read is not a licence to trust the raw shapes: the workspace root and
            // the classifier stay in play, the qualified forms simply have nothing to fold against
        }

        return new GuardedPathForms(classify, KeptForms(shapes), KeptForms(roots));
    }

    public bool NamesAProtectedPath(string? value)
    {
        // the unclassifiable answer is this class's, not the injected classifier's: the ingestion half denies a
        // null and the reader half throws on one, and three surfaces agreeing on one predicate cannot rest on
        // which of the two happened to be loaded. Deny-wins is the fold's own posture, so a value no shape can be
        // read from is protected. A blank path stays the safe residual T1 ruled — it is a value, not the absence
        // of one, and no fold reads a file out of it either.
        if (value is null)
        {
            return true;
        }

        // the shape the caller typed is asked first: it is the cheap half, and the one a built-in mask answers
        // without any folding
        if (_classify(value))
        {
            return true;
        }

        // the fold is a pure function of the recorded string, and a ledger asks the same handful of shapes
        // thousands of times over — recomputing it per row is what made stats cost 1.5x on a full log
        if (!_folded.TryGetValue(value, out var protectedByFold))
        {
            protectedByFold = FoldedFormsDeny(value);
            _folded[value] = protectedByFold;
        }

        return protectedByFold;
    }

    // every shape is a candidate rather than the first one that fits: the same file sits under a repo root and
    // the workspace root at once, and each reading of it gets asked about. Resolving a reading against every root
    // and reading it back relative to every root is what collapses a multi-segment prefix and a '..' alias in one
    // step — a prefix strip alone leaves 'infra/../infra/VaultKeys.cs' looking like nothing.
    // A value can hide a recorded shape behind a recorded shape ('/work/demo/repos/demo/infra/VaultKeys.cs' is the
    // stored row under three names the manifest owns), so the two generators get different treatment on purpose.
    // Re-rooting runs once: it can lengthen a value — 'repo/x' resolved under a repo root and read back against
    // the workspace root comes out 'repo/repo/x' — so re-rooting its own output is an unbounded generator, and the
    // first version of this walk spun for seven minutes on exactly that. Stripping runs to a fixed point: every
    // strip removes a shape and its separator, so each round is strictly shorter and the walk ends by itself,
    // slicing strings and asking nothing of the filesystem.
    private bool FoldedFormsDeny(string value)
    {
        var slashed = value.Replace('\\', '/');

        // a leading point segment names the same row as the path behind it, and it breaks every anchored reading
        // the same way a qualified one does — so it comes off alongside the value as written
        var readings = new[] { slashed };
        if (slashed.StartsWith("./", StringComparison.Ordinal))
        {
            readings = [slashed, slashed[2..]];
        }

        foreach (var reading in readings)
        {
            var frontier = new List<string>(StrippedForms(reading));
            frontier.AddRange(RebasedForms(reading));
            if (AnyDenies(frontier))
            {
                return true;
            }

            while (frontier.Count > 0)
            {
                var shorter = new List<string>();
                foreach (var form in frontier)
                {
                    shorter.AddRange(StrippedForms(form));
                }

                if (AnyDenies(shorter))
                {
                    return true;
                }

                frontier = shorter;
            }
        }

        return false;
    }

    private bool AnyDenies(IEnumerable<string> forms)
    {
        foreach (var form in forms)
        {
            if (_classify(form))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<string> StrippedForms(string candidate)
    {
        foreach (var shape in _shapes)
        {
            if (candidate.Length > shape.Length + 1
                && candidate[shape.Length] == '/'
                && candidate.StartsWith(shape, StringComparison.OrdinalIgnoreCase))
            {
                yield return candidate[(shape.Length + 1)..];
            }
        }
    }

    private IEnumerable<string> RebasedForms(string candidate)
    {
        foreach (var absolute in ResolvedForms(candidate))
        {
            foreach (var root in _roots)
            {
                if (RelativeTo(root, absolute) is { } relative)
                {
                    yield return relative;
                }
            }
        }
    }

    private IEnumerable<string> ResolvedForms(string candidate)
    {
        var resolved = new List<string>(_roots.Count);
        foreach (var root in _roots)
        {
            try
            {
                var absolute = Path.GetFullPath(candidate, root);
                if (!resolved.Contains(absolute, PathComparer))
                {
                    resolved.Add(absolute);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return resolved;
    }

    // a reading that leaves the workspace is not a shape of a stored path, and a value that resolves to the
    // root itself names no file: both stay out of the fold rather than becoming a deny
    private static string? RelativeTo(string root, string absolute)
    {
        try
        {
            var relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
            return relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal)
                || Path.IsPathRooted(relative)
                    ? null
                    : relative;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> KeptForms(IEnumerable<string> forms) =>
        [.. forms.Where(form => form.Length > 0 && form != ".")
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Trim(string path) => path.Replace('\\', '/').TrimEnd('/');
}
