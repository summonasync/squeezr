using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Config;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Commands;

public sealed class IndexCommand : Command<IndexCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--repo")]
        public string? Repo { get; set; }

        [CommandOption("--full")]
        public bool Full { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var workspaceRoot = WorkspaceRootResolver.RequireWorkspaceRoot();
            ReportConfigWarnings(workspaceRoot);

            var result = new WorkspaceIndexer().Index(
                workspaceRoot,
                settings.Repo,
                settings.Full,
                noSemantic: true,
                onStaleTakeover: StaleLockWarningSink.ToStderr);

            if (settings.Json)
            {
                JsonOutput.Write(result);
            }
            else
            {
                foreach (var repo in result.Repos)
                {
                    var ignoredPart = repo.Ignored > 0 ? $", ignored: {repo.Ignored}" : "";
                    AnsiConsole.MarkupLine(
                        $"[bold]{repo.Name}[/]: +{repo.Added} ~{repo.Modified} -{repo.Deleted} " +
                        $"(skipped: {repo.Skipped}{ignoredPart}, files: {repo.Files})");
                }

                foreach (var warning in result.Warnings)
                {
                    Console.Error.WriteLine($"warning: {warning}");
                }
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static void ReportConfigWarnings(string workspaceRoot)
    {
        foreach (var warning in LiteConfigFile.TryLoad(workspaceRoot).Warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
    }
}
