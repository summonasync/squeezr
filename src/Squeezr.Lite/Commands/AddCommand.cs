using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Commands;

public sealed class AddCommand : Command<AddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path>")]
        public string RepoPath { get; set; } = string.Empty;

        [CommandOption("--name")]
        public string? Name { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var workspaceRoot = WorkspaceRootResolver.RequireWorkspaceRoot();
            var manager = new WorkspaceManager(workspaceRoot);
            var entry = manager.AddRepo(settings.RepoPath, settings.Name, StaleLockWarningSink.ToStderr);

            var repoFullPath = Path.GetFullPath(
                Path.IsPathRooted(settings.RepoPath)
                    ? settings.RepoPath
                    : Path.Combine(workspaceRoot, settings.RepoPath));
            RegistrationAdvisoryOutput.Write(
                RegistrationAdvisories.Collect(
                    entry.Name,
                    WorkspaceCanonicalPath.Canonicalize(repoFullPath)),
                settings.Json);

            if (settings.Json)
            {
                JsonOutput.Write(entry);
                return 0;
            }

            AnsiConsole.MarkupLine($"Added repo [bold]{entry.Name.EscapeMarkup()}[/] at {entry.Path.EscapeMarkup()}");
            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }
}
