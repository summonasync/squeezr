using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorktreeResolveCommand : Command<WorktreeResolveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to resolve; defaults to the current directory")]
        [CommandArgument(0, "[PATH]")]
        public string? Path { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var target = string.IsNullOrWhiteSpace(settings.Path)
            ? WorkspaceRootResolver.LaunchDirectory()
            : settings.Path!;

        WorktreeResolveReport report;
        WorktreeGoneException? refusal;
        try
        {
            // The home check is the one this verb shares with every registry reader — a dangling profile is a
            // named refusal rather than an empty world — and GuardRegistryIo keeps a registry that goes busy
            // reading as contention instead of as a broken answer. What this verb adds is nothing: no store dir,
            // no manifest, no row of its own. The one write that can still happen behind it is the registry's own
            // recovery, measured at live scale: with 1630 store dirs present and
            // workspaces.json missing or unparseable, Load() rebuilds that document (+45 ms on a 262 ms run),
            // and with a healthy document it leaves the bytes and the mtime alone. The SessionStart hook and every
            // serve start walk the same read, so the recovery is a property of the read path, not of this verb.
            SqueezrHome.RequireHabitableProfileHome();
            report = WorktreeResolveReport.From(
                WorkspaceRootResolver.GuardRegistryIo(() => WorktreeResolve.Resolve(target)));
            refusal = null;
        }
        catch (WorktreeGoneException gone)
        {
            report = WorktreeResolveReport.FromGone(gone);
            refusal = gone;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return CommandError.Report(ex, settings.Json);
        }

        if (settings.Json)
        {
            // A gone tree answers twice on purpose, the way `worktree drop' hands a halted removal: the block on
            // stdout is the measured facts, the envelope on stderr is the refusal with its code, and exit 1 says
            // the question could not be answered. Either channel alone would be half the answer — one would make
            // a reader parse prose for the store keys, the other would let a dead checkout read as resolved.
            JsonOutput.Write(report);
            return refusal is null
                ? WriteJsonNotices(report)
                : CommandError.Report(refusal, settings.Json);
        }

        return refusal is not null
            ? CommandError.Report(refusal, settings.Json)
            : WriteHuman(report);
    }

    private static int WriteJsonNotices(WorktreeResolveReport report)
    {
        // Pending travels on the warning channel, as it does for `worktree init' and `status': a hook that reads
        // only stderr still hears that this lane's coordinates are not its branch's yet.
        foreach (var notice in report.Notices)
        {
            Console.Error.WriteLine($"warning: {notice}");
        }

        return 0;
    }

    private static int WriteHuman(WorktreeResolveReport report)
    {
        if (report.State != nameof(WorktreeResolveState.Resolved))
        {
            AnsiConsole.MarkupLine($"[yellow]{report.State.EscapeMarkup()}[/]");
            AnsiConsole.MarkupLine($"  {report.Reason!.EscapeMarkup()}");
            if (report.State == nameof(WorktreeResolveState.Unregistered))
            {
                WriteCheckout(report);
            }

            return 0;
        }

        WriteCheckout(report);
        foreach (var row in report.Rows)
        {
            WriteRow(row);
        }

        WriteParent(report);
        if (report.DependentLanes > 0)
        {
            AnsiConsole.MarkupLine(
                $"  lanes: [bold]{report.DependentLanes}[/] worktree row(s) name this tree as their parent");
        }

        foreach (var notice in report.Notices)
        {
            AnsiConsole.MarkupLine($"  [yellow]{notice.EscapeMarkup()}[/]");
        }

        return 0;
    }

    private static void WriteCheckout(WorktreeResolveReport report)
    {
        AnsiConsole.MarkupLine($"  checkout: {(report.RootPath ?? "none — no git facts here").EscapeMarkup()}");
        if (report.RootPath is null)
        {
            return;
        }

        AnsiConsole.MarkupLine(
            $"  branch: {(report.Branch ?? (report.IsDetached ? "detached head" : "unknown")).EscapeMarkup()} " +
            (report.IsLinked ? "[grey](linked worktree)[/]" : "[grey](main worktree)[/]"));
        if (report.Rows.Count > 1)
        {
            AnsiConsole.MarkupLine(
                $"  rows: {report.Rows.Count} registry rows name this checkout — the directory is the identity, " +
                "a spelling is not a second workspace");
        }
    }

    private static void WriteRow(WorktreeResolveRow row)
    {
        var kind = row.Kind switch
        {
            WorkspaceKind.Worktree => "worktree",
            WorkspaceKind.Nested => "nested",
            null => "workspace",
            _ => row.Kind.ToString()!.ToLowerInvariant(),
        };
        AnsiConsole.MarkupLine(
            $"  store: [bold]{row.StoreKey.EscapeMarkup()}[/] — {row.Name.EscapeMarkup()} ({kind})");
        AnsiConsole.MarkupLine($"    registered at: {row.RootPath.EscapeMarkup()}");
        if (row.Branch is { } recorded)
        {
            AnsiConsole.MarkupLine($"    recorded branch: {recorded.EscapeMarkup()}");
        }

        AnsiConsole.MarkupLine(row.TreePresent
            ? "    tree: [green]present[/]"
            : $"    tree: [yellow]gone[/] — no checkout at this root; reap the row with " +
              $"{RemedyText.WorkspaceRepairCommand.EscapeMarkup()}");
        AnsiConsole.MarkupLine(row.StoreDirPresent
            ? $"    store dir: [green]present[/] — collection {row.Collection.EscapeMarkup()}"
            : $"    store dir: [yellow]absent[/] — the row names no store dir; collection " +
              $"{row.Collection.EscapeMarkup()} may still be on the service");
        AnsiConsole.MarkupLine($"    serve: {row.ServeCommand.EscapeMarkup()}");
    }

    private static void WriteParent(WorktreeResolveReport report)
    {
        if (report.ParentRootPath is not { } parent)
        {
            return;
        }

        AnsiConsole.MarkupLine(report.ParentRegistered
            ? $"  parent: {parent.EscapeMarkup()} [grey](indexed workspace)[/]"
            : $"  parent: {parent.EscapeMarkup()} [yellow]not indexed[/] — a linked checkout fills from it, so " +
              $"run {RemedyText.InitCommand.EscapeMarkup()} there first");
    }
}
