using System.Net.Http;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Infra;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Infra;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class ServicesUpCommand : AsyncCommand<ServicesUpCommand.Settings>
{
    private const string PullingStage = "Pulling images (first run may download ~3.6 GB)…";
    private const string StartingStage = "Starting containers…";
    private const string StandardQdrantEndpoint = "http://localhost:6333";
    private const string OllamaInstallHint =
        "docker run -d --name ollama -v ollama:/root/.ollama -p 11434:11434 ollama/ollama";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }

        [CommandOption("--verbose")]
        public bool Verbose { get; set; }

        [CommandOption("--owned")]
        public bool Owned { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var cli = new DockerComposeCli(new ProcessRunner());
            var interactive = AnsiConsole.Profile.Capabilities.Interactive;

            if (settings.Owned || QdrantOwnedDecision.AnyWorkspaceWantsOwned())
            {
                return await UpOwnedAsync(cli, settings, cancellationToken);
            }

            var externalFound = await ProbeStandardQdrantAsync(cancellationToken);

            if (interactive)
            {
                return await DecideInteractivelyAsync(cli, externalFound, settings, cancellationToken);
            }

            return NonInteractiveFallback(externalFound, settings);
        }
        catch (ServicesException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private async Task<int> DecideInteractivelyAsync(
        DockerComposeCli cli,
        bool externalFound,
        Settings settings,
        CancellationToken cancellationToken)
    {
        await AnnounceOllamaAsync(settings, cancellationToken);

        if (!externalFound)
        {
            AnsiConsole.MarkupLine("[grey]No external Qdrant found — raising the squeezr-owned instance.[/]");
            PersistOwnedDecision(true, null);
            return await UpOwnedAsync(cli, settings, cancellationToken);
        }

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Live Qdrant found on the standard port — use it, or raise a dedicated squeezr-owned instance?")
                .AddChoices("Use the running Qdrant", "Raise a squeezr-owned instance"));

        if (choice.StartsWith("Use", StringComparison.Ordinal))
        {
            PersistOwnedDecision(false, StandardQdrantEndpoint);
            AnsiConsole.MarkupLine($"[green]Using the external Qdrant[/] at {StandardQdrantEndpoint} — recorded in config.");
            return 0;
        }

        PersistOwnedDecision(true, null);
        return await UpOwnedAsync(cli, settings, cancellationToken);
    }

    private static int NonInteractiveFallback(bool externalFound, Settings settings)
    {
        // the 069 guard: probe, hint, never raise and never block a script
        Console.Error.WriteLine(externalFound
            ? "hint: a live Qdrant was found on the standard port; to record the choice run interactively, or set qdrant.owned true/false in the workspace config."
            : "hint: no Qdrant found; pass --owned to raise the squeezr-owned instance, or set qdrant.owned true in the workspace config.");

        if (settings.Json)
        {
            JsonOutput.Write(new ServicesUpReport(
                OwnedComposeFile.FilePath,
                OwnedComposeFile.ProjectName,
                false,
                "http://localhost:6336",
                "http://localhost:6335",
                string.Empty,
                null));
        }

        return 0;
    }

    private static void PersistOwnedDecision(bool owned, string? endpoint)
    {
        foreach (var entry in WorkspaceRegistry.Load())
        {
            try
            {
                var config = ConfigFile.Load(entry.RootPath);
                if (config.Qdrant.Owned == owned &&
                    (owned || config.Qdrant.Endpoint.Length > 0))
                {
                    continue;
                }

                config = config with
                {
                    Qdrant = owned
                        ? config.Qdrant with { Owned = true }
                        : config.Qdrant with { Owned = false, Endpoint = endpoint ?? config.Qdrant.Endpoint },
                };
                ConfigFile.Save(entry.RootPath, config);
            }
            catch (Exception ex) when (ex is WorkspaceException or InvalidDataException or IOException)
            {
            }
        }
    }

    private static async Task AnnounceOllamaAsync(Settings settings, CancellationToken cancellationToken)
    {
        var status = await OllamaStatusProbe.ProbeAsync(new OllamaOptions(), cancellationToken);
        var message = status.Available
            ? "[grey]Ollama: using the running instance (squeezr never raises Ollama).[/]"
            : $"[grey]Ollama not found — squeezr never raises it. Install yourself:[/] {OllamaInstallHint}";

        if (settings.Json)
        {
            Console.Error.WriteLine(message.Replace("[grey]", string.Empty).Replace("[/]", string.Empty));
            return;
        }

        AnsiConsole.MarkupLine(message);
    }

    private static async Task<bool> ProbeStandardQdrantAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(StandardQdrantEndpoint + "/readyz", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<int> UpOwnedAsync(
        DockerComposeCli cli, Settings settings, CancellationToken cancellationToken)
    {
        var (restPort, grpcPort) = QdrantOwnedDecision.OwnedPorts();
        string composePath;
        try
        {
            composePath = OwnedComposeFile.Write(restPort, grpcPort);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ServicesException(
                $"Failed to write the owned compose file: {ex.Message}",
                ServicesException.ComposeCommandFailedCode);
        }

        var installation = await cli.DetectAsync(cancellationToken);
        WriteStage(PullingStage, settings.Json);
        await cli.PullAsync(installation, composePath, ProgressHandler(settings), cancellationToken, OwnedComposeFile.ProjectName);
        WriteStage(StartingStage, settings.Json);
        await cli.RunCheckedAsync(installation, composePath, ["up", "-d"], DockerComposeCli.UpTimeout, cancellationToken, projectName: OwnedComposeFile.ProjectName);

        var grpc = $"http://localhost:{grpcPort}";
        var rest = $"http://localhost:{restPort}";
        if (settings.Json)
        {
            JsonOutput.Write(new ServicesUpReport(
                composePath,
                OwnedComposeFile.ProjectName,
                false,
                grpc,
                rest,
                string.Empty,
                installation.Version is null ? null : $"v{installation.Version}"));
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[green]Owned Qdrant is up[/] — gRPC: {grpc} (leave qdrant.endpoint empty with qdrant.owned), REST: {rest}");
        AnsiConsole.MarkupLine($"Compose file: {composePath.EscapeMarkup()}");
        return 0;
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
