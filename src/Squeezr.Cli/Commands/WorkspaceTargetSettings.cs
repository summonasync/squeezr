using System.ComponentModel;
using Spectre.Console.Cli;

namespace Squeezr.Cli.Commands;

// The two spellings of the store axis, carried by every query verb. A base class rather than five copies of the
// same option pair because the axis and its wording must not drift apart per verb: a reader who learned
// `--at parent' on search must find the same thing on refs.
public abstract class WorkspaceTargetSettings : CommandSettings
{
    [CommandOption("--workspace <KEY|PATH>")]
    [Description("Answer from another store: its store key, or a path inside the checkout that owns it")]
    public string? Workspace { get; set; }

    [CommandOption("--at <parent|PATH>")]
    [Description("Sugar for --workspace on a checkout of this one: 'parent' points at the main worktree's store")]
    public string? At { get; set; }

    // The resolution and the naming travel together, because an answer that quietly came from another store is
    // the exact lie this axis exists to close: no verb gets to resolve a target and then not say what it hit.
    public string RequireWorkspaceRoot()
    {
        var target = WorkspaceTargetSelector.Require(Workspace, At);
        target.Report();
        return target.WorkspaceRoot;
    }
}
