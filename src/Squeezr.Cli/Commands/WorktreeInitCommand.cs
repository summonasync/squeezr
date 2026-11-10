using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorktreeInitCommand : Command<WorktreeInitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Worktree root to register; defaults to the current directory")]
        [CommandArgument(0, "[PATH]")]
        public string? Path { get; set; }

        [CommandOption("--name")]
        public string? Name { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var target = string.IsNullOrWhiteSpace(settings.Path)
                ? WorkspaceRootResolver.LaunchDirectory()
                : settings.Path!;

            var outcome = WorkspaceRootResolver.GuardRegistryIo(
                () => WorktreeRegistration.Register(target, settings.Name));

            if (outcome is { State: WorktreeRegistrationState.Registered, WorkspaceName: { } registeredName })
            {
                RegistrationAdvisoryOutput.Write(
                    RegistrationAdvisories.Collect(registeredName, outcome.RootPath),
                    settings.Json);
            }

            var report = WorktreeInitReport.From(outcome);
            if (report.RepairHint is not null)
            {
                Console.Error.WriteLine($"warning: {report.RepairHint}");
            }

            if (settings.Json)
            {
                JsonOutput.Write(report);
                return 0;
            }

            WriteHuman(report, outcome.State);
            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static void WriteHuman(WorktreeInitReport report, WorktreeRegistrationState state)
    {
        if (state is WorktreeRegistrationState.ParentNotIndexed or WorktreeRegistrationState.NoParentRootToName)
        {
            AnsiConsole.MarkupLine($"[yellow]{report.Reason!.EscapeMarkup()}[/]");
            return;
        }

        AnsiConsole.MarkupLine(state == WorktreeRegistrationState.Registered
            ? $"Initialized worktree store [bold]{report.StoreKey!.EscapeMarkup()}[/] in {report.RootPath.EscapeMarkup()}"
            : $"Worktree store [bold]{report.StoreKey!.EscapeMarkup()}[/] is already registered in " +
              $"{report.RootPath.EscapeMarkup()} — nothing created");

        if (report.Reason is not null)
        {
            AnsiConsole.MarkupLine($"  [grey]{report.Reason.EscapeMarkup()}[/]");
        }

        AnsiConsole.MarkupLine($"  branch: {BranchText(report).EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            $"  parent: {(report.ParentRootPath ?? "unknown").EscapeMarkup()}");
        AnsiConsole.MarkupLine($"  collection: {report.Collection!.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"  serve: {report.ServeCommand!.EscapeMarkup()}");
        AnsiConsole.MarkupLine(report.ReconcilePending switch
        {
            true => "  index: [yellow]empty[/] — nothing has been copied or reconciled into this store yet",
            false => "  index: has content — [bold]squeezr status[/] breaks it down",
            _ => "  index: reconcile state unknown until the store reports it",
        });
    }

    private static string BranchText(WorktreeInitReport report) =>
        report.Branch ?? (report.IsDetached ? "detached head" : "unknown");
}
