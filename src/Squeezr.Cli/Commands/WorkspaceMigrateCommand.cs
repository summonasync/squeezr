using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorkspaceMigrateCommand : AsyncCommand<WorkspaceMigrateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ROOT>")]
        public string Root { get; set; } = string.Empty;

        [Description("Relocate the legacy store without prompting; a refused move stays untouched")]
        [CommandOption("--yes")]
        public bool Yes { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!settings.Yes)
            {
                throw new McpUsageException(Refusal(settings.Root));
            }

            var result = WorkspaceStore.MigrateWithApproval(settings.Root, WorkspaceMigrationApproval.Explicit);
            if (settings.Json)
            {
                JsonOutput.Write(new WorkspaceMigrationReport(
                    result.SourceDir, result.StoreDir, result.StoreKey, result.MovedFileCount));
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"Migrated [bold]{result.SourceDir.EscapeMarkup()}[/] → {result.StoreDir.EscapeMarkup()} · " +
                    $"{result.MovedFileCount} file(s) · store key {result.StoreKey.EscapeMarkup()}.");
            }

            return Task.FromResult(0);
        }
        catch (Exception ex) when (ex is WorkspaceException or McpUsageException)
        {
            return Task.FromResult(CommandError.Report(ex, settings.Json));
        }
    }

    private static string Refusal(string root) =>
        $"Refusing to relocate the legacy store at '{root}' without confirmation: the absence of a prompt is " +
        "not consent. Re-run with --yes to approve the relocation, or --json for a machine-readable refusal.";
}
