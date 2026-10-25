using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Infra;
using Squeezr.Cli.Output;
using Squeezr.Core.Infra;

namespace Squeezr.Cli.Commands;

public sealed class ServicesDownCommand : AsyncCommand<ServicesDownCommand.Settings>
{
    private const string OllamaLeaveItAlone = "Ollama осталась, модели остались — разбирайся руками";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var legacyPath = ServicesComposeFile.ComposeFilePath;
        var ownedPath = OwnedComposeFile.FilePath;
        var hasLegacy = File.Exists(legacyPath);
        var hasOwned = File.Exists(ownedPath);
        if (!hasLegacy && !hasOwned)
        {
            return ReportNothingToStop(settings, legacyPath);
        }

        try
        {
            var cli = new DockerComposeCli(new ProcessRunner());
            var installation = await cli.DetectAsync(cancellationToken);

            // the legacy pair also holds Ollama: down targets the qdrant service only —
            // Ollama's container and models are the user's, never squeezr's to stop
            if (hasLegacy)
            {
                await cli.RunCheckedAsync(installation, legacyPath, ["down", "qdrant"], DockerComposeCli.DownTimeout, cancellationToken);
                if (settings.Json)
                {
                    JsonOutput.Write(new ServicesDownReport(legacyPath, ServicesComposeFile.ProjectName, Stopped: true));
                }
            }

            if (hasOwned)
            {
                await cli.RunCheckedAsync(installation, ownedPath, ["down"], DockerComposeCli.DownTimeout, cancellationToken, projectName: OwnedComposeFile.ProjectName);
                if (settings.Json)
                {
                    JsonOutput.Write(new ServicesDownReport(ownedPath, OwnedComposeFile.ProjectName, Stopped: true));
                }
            }

            if (settings.Json)
            {
                Console.Error.WriteLine($"note: {OllamaLeaveItAlone}");
                return 0;
            }

            AnsiConsole.MarkupLine("[green]Qdrant-side services are stopped[/]. Data volumes are kept.");
            AnsiConsole.MarkupLine($"[grey]{OllamaLeaveItAlone.EscapeMarkup()}[/]");
            return 0;
        }
        catch (ServicesException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static int ReportNothingToStop(Settings settings, string composePath)
    {
        Console.Error.WriteLine("warning: no Squeezr compose file found; run 'squeezr services up' to start the services.");
        if (settings.Json)
        {
            JsonOutput.Write(new ServicesDownReport(composePath, ServicesComposeFile.ProjectName, Stopped: false));
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Squeezr services are not running.[/]");
        }

        return 0;
    }
}
