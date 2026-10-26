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
        var legacyPath = ServicesComposeFile.ComposeFilePath;
        var ownedPath = OwnedComposeFile.FilePath;
        var hasLegacy = File.Exists(legacyPath);
        var hasOwned = File.Exists(ownedPath);
        if (!hasLegacy && !hasOwned)
        {
            return ReportNothingToStop(settings, legacyPath);
        }

        var cli = new DockerComposeCli(runner);
        var installation = await cli.DetectAsync(cancellationToken);

        // the legacy pair also holds Ollama: down targets the qdrant service only —
        // Ollama's container and models are the user's, never squeezr's to stop
        if (hasLegacy)
        {
            await cli.RunCheckedAsync(installation, legacyPath, ["down", "qdrant"], DockerComposeCli.DownTimeout, cancellationToken);
        }

        if (hasOwned)
        {
            await cli.RunCheckedAsync(
                installation, ownedPath, ["down"], DockerComposeCli.DownTimeout, cancellationToken,
                projectName: OwnedComposeFile.ProjectName);
        }

        if (settings.Json)
        {
            if (hasLegacy)
            {
                JsonOutput.Write(new ServicesDownReport(legacyPath, ServicesComposeFile.ProjectName, Stopped: true));
            }

            if (hasOwned)
            {
                JsonOutput.Write(new ServicesDownReport(ownedPath, OwnedComposeFile.ProjectName, Stopped: true));
            }

            Console.Error.WriteLine($"note: {OllamaLeaveItAlone}");
            return 0;
        }

        AnsiConsole.MarkupLine("[green]Qdrant-side services are stopped[/]. Data volumes are kept.");
        AnsiConsole.MarkupLine($"[grey]{OllamaLeaveItAlone.EscapeMarkup()}[/]");
        await ReportStillAnsweringAsync(AnsiConsole.Console, livenessProbe ?? new ServiceLivenessProbe(), cancellationToken);
        return 0;
    }

    // Down only stops our compose projects' qdrant side. An adopted external instance keeps
    // answering the port - staying silent about it would read as "we stopped everything"
    // (spec 069 §5.2).
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
