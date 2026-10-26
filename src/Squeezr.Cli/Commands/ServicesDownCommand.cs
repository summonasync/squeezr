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
        try
        {
            return await RunCoreAsync(settings, new ProcessRunner(), null, cancellationToken);
        }
        catch (ServicesException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    internal static async Task<int> RunCoreAsync(
        Settings settings,
        IProcessRunner runner,
        ServiceLivenessProbe? livenessProbe,
        CancellationToken cancellationToken)
    {
        var composePath = ServicesComposeFile.ComposeFilePath;
        if (!File.Exists(composePath))
        {
            return ReportNothingToStop(settings, composePath);
        }

        var cli = new DockerComposeCli(runner);
        var installation = await cli.DetectAsync(cancellationToken);
        await cli.RunCheckedAsync(installation, composePath, ["down"], DockerComposeCli.DownTimeout, cancellationToken);

        if (settings.Json)
        {
            JsonOutput.Write(new ServicesDownReport(composePath, ServicesComposeFile.ProjectName, Stopped: true));
            return 0;
        }

        AnsiConsole.MarkupLine("[green]Squeezr services are stopped[/] (Qdrant, Ollama). Data volumes are kept.");
        await ReportStillAnsweringAsync(AnsiConsole.Console, livenessProbe ?? new ServiceLivenessProbe(), cancellationToken);
        return 0;
    }

    // Down only stops our compose project. An adopted external instance keeps answering the port -
    // staying silent about it would read as "we stopped everything" (spec 069 §5.2).
    internal static async Task ReportStillAnsweringAsync(
        IAnsiConsole console, ServiceLivenessProbe probe, CancellationToken cancellationToken)
    {
        foreach (var probeSpec in new[] { ManagedServiceProbe.Ollama, ManagedServiceProbe.Qdrant })
        {
            var liveness = await probe.ProbeAsync(probeSpec, cancellationToken);
            if (liveness.Kind == ServiceLiveness.Outcome.Answered)
            {
                console.MarkupLine(
                    $"[yellow]{probeSpec.ServiceName.EscapeMarkup()} is still answering on " +
                    $"{probeSpec.Endpoint.ToString().EscapeMarkup()} — that instance was not ours, it was left untouched.[/]");
            }
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
