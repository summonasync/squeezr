using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Onboarding;

// Where the project block belongs, and which store would answer an agent standing there. One repo has several
// roots once worktrees are in play, so "the workspace above me" is not the project: inside an indexed repo the
// project is the checkout you are standing in, and its own AGENTS.md is the file that checkout's agents read.
// Where nothing is indexed at all the block stays where it has always gone — the directory the operator is
// standing in — because inventing a project root for an unindexed repo is not this rule's business.
//
// The store is then asked of the registry through the same nearest-child rule serve and the hook use, so a lane
// that has been registered answers for itself here too, and the wording only ever points at a store that
// really exists above the checkout.
public sealed record ProjectCheckout(string Root, string? ServedByRoot, string? ServedByStoreKey)
{
    public static ProjectCheckout From(string launchDirectory, WorkspaceResolution resolution)
    {
        var root = resolution.IsWorkspace
            && WorktreeFacts.Resolve(launchDirectory)?.Toplevel is { } toplevel
            && !WorkspaceCanonicalPath.SamePhysicalPath(toplevel, resolution.RootPath)
                ? toplevel
                : resolution.RootPath;

        // A row naming this exact root is the checkout owning a store. Ask that first: FindContainingPath answers
        // for a path *inside* a root, so feeding it the root itself would step up to its parent and hand a
        // registered lane back as its parent's tenant. Only when no row names the root does the question become
        // "whose store answers for files here", and that is the nearest-child rule serve and the hook use.
        if (WorkspaceRegistry.FindByRootPath(root) is not null)
        {
            return new(root, null, null);
        }

        var serving = WorkspaceRegistry.FindContainingPath(
            Path.Combine(root, ProjectInstructionTargets.CarrierFiles[0]));
        return serving is null
            ? new(root, null, null)
            : new(root, serving.RootPath, serving.StoreKey);
    }

    public string Block => ServedByRoot is { } other
        ? ProjectInstructions.BlockServedByOtherStore(other, ServedByStoreKey)
        : ProjectInstructions.Block;
}
