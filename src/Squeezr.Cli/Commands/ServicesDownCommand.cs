using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Infra;
using Squeezr.Cli.Output;
using Squeezr.Core.Infra;

namespace Squeezr.Cli.Commands;

public sealed class ServicesDownCommand : AsyncCommand<ServicesDownCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var composePath = ServicesComposeFile.ComposeFilePath;
        if (!File.Exists(composePath))
        {
            return ReportNothingToStop(settings, composePath);
        }

        try
        {
            var cli = new DockerComposeCli(new ProcessRunner());
            var installation = await cli.DetectAsync(cancellationToken);
            await cli.RunCheckedAsync(installation, composePath, ["down"], DockerComposeCli.DownTimeout, cancellationToken);

            if (settings.Json)
            {
                JsonOutput.Write(new ServicesDownReport(composePath, ServicesComposeFile.ProjectName, Stopped: true));
                return 0;
            }

            AnsiConsole.MarkupLine("[green]Squeezr services are stopped[/] (Qdrant, Ollama). Data volumes are kept.");
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
