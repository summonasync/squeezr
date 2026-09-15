using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Infra;
using Squeezr.Cli.Output;
using Squeezr.Core.Infra;

namespace Squeezr.Cli.Commands;

public sealed class ServicesUpCommand : AsyncCommand<ServicesUpCommand.Settings>
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
            await cli.RunCheckedAsync(installation, composePath, ["up", "-d"], DockerComposeCli.UpTimeout, cancellationToken);

            if (settings.Json)
            {
                JsonOutput.Write(new ServicesUpReport(
                    composePath,
                    ServicesComposeFile.ProjectName,
                    rocm,
                    ServicesComposeFile.QdrantEndpoint,
                    ServicesComposeFile.OllamaEndpoint,
                    installation.Version is null ? null : $"v{installation.Version}"));
                return 0;
            }

            AnsiConsole.MarkupLine(
                $"[green]Squeezr services are up[/] — Qdrant: {ServicesComposeFile.QdrantEndpoint}, " +
                $"Ollama: {ServicesComposeFile.OllamaEndpoint} (ROCm {(rocm ? "enabled" : "disabled — running on CPU")})");
            AnsiConsole.MarkupLine($"Compose file: {composePath.EscapeMarkup()}");
            return 0;
        }
        catch (ServicesException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }
}
