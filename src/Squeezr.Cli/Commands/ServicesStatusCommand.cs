using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Infra;
using Squeezr.Cli.Output;
using Squeezr.Core.Infra;
using Squeezr.Semantic.Infra;

namespace Squeezr.Cli.Commands;

public sealed class ServicesStatusCommand : AsyncCommand<ServicesStatusCommand.Settings>
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
            return await RunCoreAsync(settings, new ProcessRunner(), null, null, cancellationToken);
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
        IServiceOwnershipSource? ownershipSource,
        CancellationToken cancellationToken)
    {
        var composePath = ServicesComposeFile.ComposeFilePath;
        var rocm = ServicesComposeFile.TryReadRocmEnabled() ?? new RocmDeviceProbe().AreAvailable();
        IReadOnlyList<ComposeServiceState> states = [];
        string? composeVersion = null;
        if (File.Exists(composePath))
        {
            var cli = new DockerComposeCli(runner);
            var installation = await cli.DetectAsync(cancellationToken);
            composeVersion = FormatComposeVersion(installation);
            var result = await cli.RunCheckedAsync(
                installation, composePath, ["ps", "--format", "json"], DockerComposeCli.StatusTimeout, cancellationToken);
            states = DockerComposeCli.ParseServiceStates(result.StdOut, installation.Version);
        }
        else
        {
            Console.Error.WriteLine("warning: no Squeezr compose file found; run 'squeezr services up' to start the services.");
        }

        // Probe-first honesty (069): the compose table alone says nothing about a foreign instance
        // already serving a port. The T1 probe runs for both managed services regardless of the
        // compose state, so a live external instance is never rendered as a bare "not running".
        var probeStates = await ExternalServiceStateSolver.SolveAsync(
            ManagedServiceProbe.All,
            livenessProbe ?? new ServiceLivenessProbe(),
            ownershipSource ?? new ComposeOwnershipSource(new DockerComposeCli(runner), runner, composePath),
            cancellationToken);

        var report = new ServicesStatusReport(
            composePath,
            ServicesComposeFile.ProjectName,
            rocm,
            composeVersion,
            states.Select(state => new ServiceInstanceEntry(state.Service, state.Name, state.State, state.Status)).ToList());
        if (settings.Json)
        {
            JsonOutput.Write(report);
            return 0;
        }

        RenderHuman(AnsiConsole.Console, report, probeStates);
        return 0;
    }

    private static string? FormatComposeVersion(DockerComposeInstallation installation) =>
        installation.Version is null ? null : $"v{installation.Version} ({(installation.Plugin ? "plugin" : "standalone")})";

    internal static void RenderHuman(
        IAnsiConsole console, ServicesStatusReport report, IReadOnlyList<ExternalServiceState> probeStates)
    {
        if (report.ComposeVersion is not null)
        {
            console.MarkupLine($"Docker Compose: {report.ComposeVersion.EscapeMarkup()}");
        }

        if (report.Services.Count == 0)
        {
            console.MarkupLine("[grey]No Squeezr services are running.[/]");
        }
        else
        {
            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("Service");
            table.AddColumn("Container");
            table.AddColumn("State");
            table.AddColumn("Status");
            foreach (var service in report.Services)
            {
                table.AddRow(
                    service.Service.EscapeMarkup(),
                    service.Name.EscapeMarkup(),
                    service.State.EscapeMarkup(),
                    service.Status.EscapeMarkup());
            }

            console.Write(table);
        }

        foreach (var probe in probeStates)
        {
            var line = probe.State switch
            {
                ServiceProbeState.Managed => $"{probe.Probe.ServiceName}: managed (our compose project)",
                ServiceProbeState.External => $"{probe.Probe.ServiceName}: external" +
                    (probe.Owner is null ? " (owner unknown)" : $" — served by {probe.Owner}") +
                    " — squeezr will not duplicate it",
                ServiceProbeState.Unknown => $"{probe.Probe.ServiceName}: unknown — the probe failed after retry; " +
                    "check the endpoint manually before starting anything",
                _ => $"{probe.Probe.ServiceName}: absent — start it with: squeezr services up"
            };
            console.MarkupLine($"[grey]{line.EscapeMarkup()}[/]");
        }
    }
}
