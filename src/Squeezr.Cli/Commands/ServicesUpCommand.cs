using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Infra;
using Squeezr.Cli.Output;
using Squeezr.Core.Infra;

namespace Squeezr.Cli.Commands;

public sealed class ServicesUpCommand : AsyncCommand<ServicesUpCommand.Settings>
{
    private const string PullingStage = "Pulling images (first run may download ~3.6 GB)…";
    private const string StartingStage = "Starting containers…";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }

        [CommandOption("--verbose")]
        public bool Verbose { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var cli = new DockerComposeCli(new ProcessRunner());
            var rocm = new RocmDeviceProbe().AreAvailable();
            string composePath;
            try
            {
                composePath = ServicesComposeFile.Write(rocm);
            }
            catch (IOException ex)
            {
                throw new ServicesException(
                    $"Failed to write the Squeezr compose file: {ex.Message}",
                    ServicesException.ComposeCommandFailedCode);
            }

            var installation = await cli.DetectAsync(cancellationToken);
            WriteStage(PullingStage, settings.Json);
            await cli.PullAsync(installation, composePath, ProgressHandler(settings), cancellationToken);
            WriteStage(StartingStage, settings.Json);
            await cli.RunCheckedAsync(installation, composePath, ["up", "-d"], DockerComposeCli.UpTimeout, cancellationToken);

            if (settings.Json)
            {
                JsonOutput.Write(new ServicesUpReport(
                    composePath,
                    ServicesComposeFile.ProjectName,
                    rocm,
                    ServicesComposeFile.QdrantGrpcEndpoint,
                    ServicesComposeFile.QdrantRestEndpoint,
                    ServicesComposeFile.OllamaEndpoint,
                    installation.Version is null ? null : $"v{installation.Version}"));
                return 0;
            }

            AnsiConsole.MarkupLine(
                $"[green]Squeezr services are up[/] — Qdrant: {ServicesComposeFile.QdrantGrpcEndpoint} " +
                $"(gRPC — use this in qdrant.endpoint), REST: {ServicesComposeFile.QdrantRestEndpoint}, " +
                $"Ollama: {ServicesComposeFile.OllamaEndpoint} (ROCm {(rocm ? "enabled" : "disabled — running on CPU")})");
            AnsiConsole.MarkupLine($"Compose file: {composePath.EscapeMarkup()}");
            return 0;
        }
        catch (ServicesException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static Action<string>? ProgressHandler(Settings settings) =>
        settings.Verbose ? chunk => Console.Error.Write(chunk) : null;

    private static void WriteStage(string message, bool json)
    {
        if (json)
        {
            Console.Error.WriteLine(message);
            return;
        }

        AnsiConsole.MarkupLine($"[grey]{message}[/]");
    }
}
