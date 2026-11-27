using System.ComponentModel;
using Spectre.Console.Cli;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Commands;

public abstract class WorkspaceTargetSettings : CommandSettings
{
    [CommandOption("--workspace <KEY|PATH>")]
    [Description("Answer from another store: its store key, or a path inside the checkout that owns it")]
    public string? Workspace { get; set; }

    [CommandOption("--at <parent|PATH>")]
    [Description("Sugar for --workspace on a checkout of this one: 'parent' points at the main worktree's store")]
    public string? At { get; set; }

    public string RequireWorkspaceRoot()
    {
        var target = WorkspaceTargetSelector.Require(Workspace, At);
        target.Report();
        return target.WorkspaceRoot;
    }
}

public sealed record WorkspaceTarget(string WorkspaceRoot, AnswerSubject? Subject)
{
    public void Report() => AnswerSubjectNotice.Report(Subject);
}
