using System.ComponentModel;
using System.Net;
using System.Text.Json;
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
    private const string AdoptModelName = "nomic-embed-text";
    private const string QdrantInterimHint =
        "squeezr is moving to its own dedicated Qdrant instance (078); meanwhile remove the foreign " +
        "container or wait for 078.";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }

        [CommandOption("--verbose")]
        public bool Verbose { get; set; }

        [CommandOption("--owned")]
        public bool Owned { get; set; }

        [Description("Start our containers over whatever the probe found; a port clash fails loudly")]
        [CommandOption("--managed")]
        public bool Managed { get; set; }

        [Description("Print the per-service decisions and touch nothing")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var runner = new ProcessRunner();
            var cli = new DockerComposeCli(runner);

            if (settings.Owned || QdrantOwnedDecision.AnyWorkspaceWantsOwned())
            {
                return await RunOwnedCoreAsync(settings, cli, cancellationToken);
            }

            if (AnsiConsole.Profile.Capabilities.Interactive)
            {
                return await DecideUseOrOwnAsync(settings, cli, cancellationToken);
            }

            return await RunCoreAsync(
                settings, cli, runner, ensureHandler: null, versionProbe: null, livenessProbe: null,
                cancellationToken);
        }
        catch (ServicesException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    internal static async Task<int> RunOwnedCoreAsync(
        Settings settings,
        DockerComposeCli cli,
        CancellationToken cancellationToken)
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
        await cli.RunCheckedAsync(
            installation, composePath, ["up", "-d"], DockerComposeCli.UpTimeout, cancellationToken,
            projectName: OwnedComposeFile.ProjectName);

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
                installation.Version is null ? null : $"v{installation.Version}",
                [new ServicesUpServiceReport("qdrant", "running", OwnedComposeFile.ProjectName, "pulled and started")]));
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[green]Owned Qdrant is up[/] — gRPC: {grpc} (leave qdrant.endpoint empty with qdrant.owned), REST: {rest}");
        AnsiConsole.MarkupLine($"Compose file: {composePath.EscapeMarkup()}");
        return 0;
    }

    private async Task<int> DecideUseOrOwnAsync(
        Settings settings,
        DockerComposeCli cli,
        CancellationToken cancellationToken)
    {
        await AnnounceOllamaAsync(settings, cancellationToken);

        if (!await ProbeStandardQdrantAsync(cancellationToken))
        {
            AnsiConsole.MarkupLine("[grey]No external Qdrant found — raising the squeezr-owned instance.[/]");
            PersistOwnedDecision(true, null);
            return await RunOwnedCoreAsync(settings, cli, cancellationToken);
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
        return await RunOwnedCoreAsync(settings, cli, cancellationToken);
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
                // an unreadable workspace cannot record the decision but must not break the raise
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

    internal static async Task<int> RunCoreAsync(
        Settings settings,
        DockerComposeCli cli,
        IProcessRunner runner,
        HttpMessageHandler? ensureHandler,
        Func<CancellationToken, Task<string?>>? versionProbe,
        ServiceLivenessProbe? livenessProbe,
        CancellationToken cancellationToken)
    {
        var decisions = await PlanAsync(
            cli, runner, livenessProbe ?? new ServiceLivenessProbe(), settings.Managed, cancellationToken);

        if (settings.DryRun)
        {
            foreach (var decision in decisions)
            {
                WriteDecision(decision, settings.Json, dryRun: true);
            }

            if (settings.Json)
            {
                JsonOutput.Write(ServicesUpReportFrom(
                    decisions, ServicesComposeFile.ComposeFilePath, null, null, [], dryRun: true, adoptExecuted: false));
            }

            return 0;
        }

        var toStart = decisions.Where(decision => decision.Executes).ToList();
        var adoptDecisions = decisions.Where(decision => decision.Action == DecisionAction.AdoptOllama).ToList();
        var composePath = ServicesComposeFile.ComposeFilePath;
        var rocm = toStart.Count > 0 && new RocmDeviceProbe().AreAvailable();
        DockerComposeInstallation installation;
        try
        {
            if (toStart.Count > 0)
            {
                composePath = WriteCompose(rocm);

                try
                {
                    installation = await cli.DetectAsync(cancellationToken);
                }
                catch (ServicesException ex) when (ex.Code == ServicesException.DockerUnavailableCode)
                {
                    throw new ServicesException(DockerMissingMessage(decisions, toStart), ex.Code, ex);
                }

                WriteStage(PullingStage, settings.Json);

                // Everything starts - byte-identical to the pre-069 flow, so the legacy pull policy
                // fallback and the single up invocation survive untouched. A mixed fleet (managed,
                // adopted or blocked services beside absent ones) is where per-service invocations
                // apply: they keep a half-fleet from dragging the other half (plan R4).
                if (toStart.Count == decisions.Count)
                {
                    await cli.PullAsync(installation, composePath, ProgressHandler(settings), cancellationToken);
                    WriteStage(StartingStage, settings.Json);
                    await cli.RunCheckedAsync(
                        installation, composePath, ["up", "-d"], DockerComposeCli.UpTimeout, cancellationToken);
                }
                else
                {
                    foreach (var decision in toStart)
                    {
                        await cli.PullServiceAsync(
                            installation, composePath, decision.Service, ProgressHandler(settings), cancellationToken);
                    }

                    WriteStage(StartingStage, settings.Json);
                    foreach (var decision in toStart)
                    {
                        await cli.RunCheckedAsync(
                            installation,
                            composePath,
                            ["up", "-d", decision.Service],
                            DockerComposeCli.UpTimeout,
                            cancellationToken,
                            ProgressHandler(settings));
                    }
                }
            }
            else
            {
                composePath = ServicesComposeFile.ComposeFilePath;
                installation = new DockerComposeInstallation("docker", Plugin: true, Version: null);
            }
        }
        catch (ServicesException ex)
        {
            // Half-started compose (plan R4): whatever did start is reported through a best-effort
            // compose ps next to the error, on the error channel. Adoption never happened - the
            // start phase failed before the ensure loop was reached.
            var finalStates = await TryReadComposeStatesAsync(cli, composePath, cancellationToken);
            if (settings.Json)
            {
                JsonOutput.Write(ServicesUpReportFrom(
                    decisions, composePath, rocm, null, finalStates, dryRun: false, adoptExecuted: false));
            }

            CommandError.Report(ex, settings.Json);
            return 1;
        }

        foreach (var decision in adoptDecisions)
        {
            var version = versionProbe is null
                ? await FetchExternalOllamaVersionAsync(cancellationToken)
                : await versionProbe(cancellationToken);
            var ensureOutcome = EnsureExternalOllamaModel(decision.Owner, ensureHandler);
            decision.FinalDetail = version is null
                ? ensureOutcome
                : $"{ensureOutcome} (external version {version})";
        }

        var runningServices = await TryReadComposeStatesAsync(cli, composePath, cancellationToken);
        var report = ServicesUpReportFrom(
            decisions, composePath, toStart.Count > 0 ? rocm : ServicesComposeFile.TryReadRocmEnabled(),
            installation.Version is null ? null : $"v{installation.Version}", runningServices,
            dryRun: false, adoptExecuted: true);

        if (settings.Json)
        {
            JsonOutput.Write(report);
        }
        else
        {
            foreach (var decision in decisions)
            {
                WriteDecision(decision, json: false, dryRun: false);
            }

            if (toStart.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[green]Squeezr services are up[/] — Qdrant: {ServicesComposeFile.QdrantGrpcEndpoint} " +
                    $"(gRPC — use this in qdrant.endpoint), REST: {ServicesComposeFile.QdrantRestEndpoint}, " +
                    $"Ollama: {ServicesComposeFile.OllamaEndpoint} (ROCm {(rocm ? "enabled" : "disabled — running on CPU")})");
                AnsiConsole.MarkupLine($"Compose file: {composePath.EscapeMarkup()}");
            }
        }

        return 0;
    }

    internal static async Task<List<ServiceDecision>> PlanAsync(
        DockerComposeCli cli,
        IProcessRunner runner,
        ServiceLivenessProbe livenessProbe,
        bool managedOverride,
        CancellationToken cancellationToken)
    {
        var ownership = new ComposeOwnershipSource(cli, runner, ServicesComposeFile.ComposeFilePath);
        var states = await ExternalServiceStateSolver.SolveAsync(
            ManagedServiceProbe.All, livenessProbe, ownership, cancellationToken);
        return states.Select(state => Decide(state, managedOverride)).ToList();
    }

    internal static ServiceDecision Decide(ExternalServiceState state, bool managedOverride)
    {
        if (managedOverride)
        {
            return new ServiceDecision(state, DecisionAction.Start, null);
        }

        return state.State switch
        {
            ServiceProbeState.Managed => new ServiceDecision(state, DecisionAction.SkipManaged, state.Owner),
            ServiceProbeState.External when state.Probe.ServiceName == "ollama" =>
                new ServiceDecision(state, DecisionAction.AdoptOllama, state.Owner),
            ServiceProbeState.External => new ServiceDecision(state, DecisionAction.RefuseExternalQdrant, state.Owner),
            ServiceProbeState.Unknown => new ServiceDecision(state, DecisionAction.BlockUnknown, null),
            _ => new ServiceDecision(state, DecisionAction.Start, null),
        };
    }

    // Spec 069 §3.3/§6: when docker is missing the error must name the docker-dependent half and
    // spare the services that are served without docker (adopted external, kept external, already
    // ours) - never a bare "both require it".
    private static string DockerMissingMessage(IReadOnlyList<ServiceDecision> decisions, IReadOnlyList<ServiceDecision> toStart)
    {
        var needed = decisions
            .Where(decision => decision.Executes)
            .Select(decision => decision.Service)
            .ToList();
        var spared = decisions
            .Where(decision => !decision.Executes)
            .Select(decision => decision.Service)
            .ToList();
        var sparedTail = spared.Count > 0
            ? $" Not docker-dependent, left as they are: {string.Join(", ", spared)}."
            : string.Empty;
        return $"Docker is required for: {string.Join(", ", needed)}.{sparedTail} " +
               "Install Docker Engine: https://docs.docker.com/engine/install/";
    }

    private static async Task<string?> FetchExternalOllamaVersionAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeoutScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutScope.CancelAfter(TimeSpan.FromSeconds(ServiceLivenessProbe.TimeoutSeconds));
                using var handler = new SocketsHttpHandler { UseProxy = false };
                using var client = new HttpClient(handler);
                using var response = await client.GetAsync(
                    $"{ServicesComposeFile.OllamaEndpoint}/api/version",
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutScope.Token);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeoutScope.Token));
                return document.RootElement.TryGetProperty("version", out var version)
                    ? version.GetString()
                    : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= ServiceLivenessProbe.RetryCount)
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                return null;
            }
        }
    }

    private static string EnsureExternalOllamaModel(string? owner, HttpMessageHandler? ensureHandler)
    {
        Console.Error.WriteLine(
            $"Ensuring embedding model '{AdoptModelName}' on the external Ollama " +
            $"'{owner ?? "unknown owner"}' at {ServicesComposeFile.OllamaEndpoint}…");
        try
        {
            OllamaModelEnsurer.Ensure(
                new OllamaOptions
                {
                    Endpoint = ServicesComposeFile.OllamaEndpoint,
                    Model = AdoptModelName
                },
                ensureHandler);
        }
        catch (Exception ex) when (ex is WorkspaceException or JsonException)
        {
            return $"model ensure failed, reported and not retried: {ex.Message}";
        }

        return ModelPresentOnExternal(ensureHandler) switch
        {
            ModelTagPresence.Present => $"model '{AdoptModelName}' ready on the external instance (api/tags re-checked)",
            ModelTagPresence.Missing => $"model '{AdoptModelName}' still missing on the external instance after ensure (api/tags re-checked)",
            _ => $"model '{AdoptModelName}' could not verify on the external instance (api/tags unreachable)"
        };
    }

    internal enum ModelTagPresence
    {
        Present,
        Missing,
        Unverifiable
    }

    // Real ollama tags carry the registry tag ("nomic-embed-text:latest"), so the match is by name
    // prefix up to the colon - the same semantics as the ensurer's own HasModel. A tags endpoint
    // that cannot be read is "could not verify", never a silent "missing".
    internal static ModelTagPresence ModelPresentOnExternal(HttpMessageHandler? handler)
    {
        try
        {
            using var http = handler is null
                ? new HttpClient(new SocketsHttpHandler { UseProxy = false })
                : new HttpClient(handler);
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{ServicesComposeFile.OllamaEndpoint}/api/tags");
            using var response = http.Send(request);
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains($"\"{AdoptModelName}\"", StringComparison.Ordinal) ||
                   body.Contains($"\"{AdoptModelName}:", StringComparison.Ordinal)
                ? ModelTagPresence.Present
                : ModelTagPresence.Missing;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ModelTagPresence.Unverifiable;
        }
    }

    private static async Task<IReadOnlyList<ComposeServiceState>?> TryReadComposeStatesAsync(
        DockerComposeCli cli, string composePath, CancellationToken cancellationToken)
    {
        try
        {
            var installation = await cli.DetectAsync(cancellationToken);
            var ps = await cli.RunAsync(
                installation, composePath, ["ps", "--format", "json"], DockerComposeCli.StatusTimeout, cancellationToken);
            return ps.ExitCode == 0 ? DockerComposeCli.ParseServiceStates(ps.StdOut, installation.Version) : null;
        }
        catch (Exception ex) when (ex is ServicesException or TimeoutException)
        {
            return null;
        }
    }

    private static ServicesUpReport ServicesUpReportFrom(
        IReadOnlyList<ServiceDecision> decisions,
        string composePath,
        bool? rocm,
        string? composeVersion,
        IReadOnlyList<ComposeServiceState>? runningServices,
        bool dryRun,
        bool adoptExecuted)
    {
        var running = runningServices?
            .Where(state => string.Equals(state.State, "running", StringComparison.OrdinalIgnoreCase))
            .Select(state => state.Service)
            .ToHashSet(StringComparer.Ordinal);

        var services = decisions.Select(decision =>
        {
            var (state, action, detail) = Describe(decision, dryRun, running, adoptExecuted);
            return new ServicesUpServiceReport(decision.Service, state, decision.Owner, action, detail);
        }).ToList();

        return new ServicesUpReport(
            composePath,
            ServicesComposeFile.ProjectName,
            rocm ?? ServicesComposeFile.TryReadRocmEnabled() ?? false,
            ServicesComposeFile.QdrantGrpcEndpoint,
            ServicesComposeFile.QdrantRestEndpoint,
            ServicesComposeFile.OllamaEndpoint,
            composeVersion,
            services);
    }

    private static (string State, string Action, string? Detail) Describe(
        ServiceDecision decision, bool dryRun, HashSet<string>? running, bool adoptExecuted)
    {
        if (dryRun)
        {
            return decision.Action switch
            {
                DecisionAction.Start => ("would-start", "pull and start", null),
                DecisionAction.SkipManaged => ("managed", "already up (managed)", null),
                DecisionAction.AdoptOllama => ("would-adopt", $"adopt external Ollama: {decision.Owner ?? "unknown owner"}", null),
                DecisionAction.RefuseExternalQdrant => ("refused",
                    $"kept foreign Qdrant: {decision.Owner ?? "unknown owner"} — {QdrantInterimHint}", null),
                _ => ("blocked", "not started: probe failed after retry — check the endpoint manually",
                    decision.ProbeState.Detail),
            };
        }

        switch (decision.Action)
        {
            case DecisionAction.Start:
                if (running is null)
                {
                    return ("unknown", "started; final state unknown (compose ps unavailable)", null);
                }

                return running.Contains(decision.Service)
                    ? ("running", "pulled and started", null)
                    : ("start-failed", "compose did not report this service running", decision.FinalDetail);
            case DecisionAction.AdoptOllama:
                return adoptExecuted
                    ? ("external-adopted", $"adopted external Ollama: {decision.Owner ?? "unknown owner"}", decision.FinalDetail)
                    : ("blocked", "not reached: the start phase failed before adoption", null);
            case DecisionAction.RefuseExternalQdrant:
                return ("external-refused",
                    $"kept foreign Qdrant: {decision.Owner ?? "unknown owner"} — {QdrantInterimHint}", null);
            case DecisionAction.SkipManaged:
                return ("managed", "already up (managed)", null);
            default:
                return ("unknown", "not started: probe failed after retry — check the endpoint manually",
                    decision.ProbeState.Detail);
        }
    }

    private static void WriteDecision(ServiceDecision decision, bool json, bool dryRun)
    {
        var prefix = dryRun ? "dry run: " : string.Empty;
        var line = decision.Action switch
        {
            DecisionAction.Start => $"{prefix}{decision.Service}: pull and start",
            DecisionAction.SkipManaged => $"{prefix}{decision.Service}: already up (managed)",
            DecisionAction.AdoptOllama => $"{prefix}{decision.Service}: adopting external Ollama " +
                $"({decision.Owner ?? "unknown owner"})",
            DecisionAction.RefuseExternalQdrant => $"{prefix}{decision.Service}: foreign Qdrant kept — {QdrantInterimHint}",
            _ => $"{prefix}{decision.Service}: not started — probe failed after retry, check the endpoint manually",
        };

        if (json)
        {
            Console.Error.WriteLine(line);
        }
        else
        {
            AnsiConsole.MarkupLine($"[grey]{line.EscapeMarkup()}[/]");
        }
    }

    private static string WriteCompose(bool rocm)
    {
        try
        {
            return ServicesComposeFile.Write(rocm);
        }
        catch (IOException ex)
        {
            throw new ServicesException(
                $"Failed to write the Squeezr compose file: {ex.Message}",
                ServicesException.ComposeCommandFailedCode);
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

    internal enum DecisionAction
    {
        Start,
        SkipManaged,
        AdoptOllama,
        RefuseExternalQdrant,
        BlockUnknown
    }

    internal sealed record ServiceDecision(
        ExternalServiceState ProbeState,
        DecisionAction Action,
        string? Owner)
    {
        public string Service => ProbeState.Probe.ServiceName;

        public bool Executes => Action == DecisionAction.Start;

        public string? FinalDetail { get; internal set; }
    }

    private static class EqualityComparerFactory
    {
        public static IEqualityComparer<T> Create<T>(Func<T?, T?, bool> equals, Func<T, int> hash) where T : class =>
            new DelegatingEqualityComparer<T>(equals, hash);

        private sealed class DelegatingEqualityComparer<T>(Func<T?, T?, bool> equals, Func<T, int> hash)
            : IEqualityComparer<T> where T : class
        {
            public bool Equals(T? left, T? right) => left is not null && right is not null && equals(left, right);

            public int GetHashCode(T obj) => hash(obj);
        }
    }
}
