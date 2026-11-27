using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Commands;

public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--name")]
        public string? Name { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var currentDirectory = Directory.GetCurrentDirectory();
            var resolution = WorkspaceRootResolver.ResolveCurrentDirectory();
            if (resolution.IsWorkspace
                && !resolution.ViaClaudeProjectDir
                && !IsSamePath(resolution.RootPath, currentDirectory))
            {
                throw new WorkspaceException(
                    $"Cannot initialize: already inside workspace '{resolution.RootPath}'.");
            }

            var result = WorkspaceRootResolver.GuardRegistryIo(
                () => new WorkspaceManager(currentDirectory).Init(settings.Name));

            if (result.RepairHint is not null)
            {
                Console.Error.WriteLine($"warning: {result.RepairHint}");
            }

            RegistrationAdvisoryOutput.Write(
                RegistrationAdvisories.Collect(result.WorkspaceName, result.RootPath),
                settings.Json);

            if (settings.Json)
            {
                JsonOutput.Write(result);
                return 0;
            }

            AnsiConsole.MarkupLine($"Initialized workspace [bold]{result.WorkspaceName.EscapeMarkup()}[/] in {result.RootPath}");
            foreach (var file in result.CreatedFiles)
            {
                AnsiConsole.MarkupLine($"  [green]+[/] {file}");
            }

            AnsiConsole.MarkupLine("Next: [bold]sqzrlite add <path> --name <repo>[/]");
            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
