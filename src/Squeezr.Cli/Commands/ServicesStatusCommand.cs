using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Infra;
using Squeezr.Cli.Output;
using Squeezr.Core.Infra;

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
        var composePath = ServicesComposeFile.ComposeFilePath;
        var rocm = ServicesComposeFile.TryReadRocmEnabled() ?? new RocmDeviceProbe().AreAvailable();
        IReadOnlyList<ComposeServiceState> states = [];
        string? composeVersion = null;
        if (File.Exists(composePath))
        {
            try
            {
                var cli = new DockerComposeCli(new ProcessRunner());
                var installation = await cli.DetectAsync(cancellationToken);
                composeVersion = FormatComposeVersion(installation);
                var result = await cli.RunCheckedAsync(
                    installation, composePath, ["ps", "--format", "json"], DockerComposeCli.StatusTimeout, cancellationToken);
                states = DockerComposeCli.ParseServiceStates(result.StdOut, installation.Version);
            }
            catch (ServicesException ex)
            {
                return CommandError.Report(ex, settings.Json);
            }
        }
        else
        {
            Console.Error.WriteLine("warning: no Squeezr compose file found; run 'squeezr services up' to start the services.");
        }

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

        RenderHuman(report);
        return 0;
    }

    private static string? FormatComposeVersion(DockerComposeInstallation installation) =>
        installation.Version is null ? null : $"v{installation.Version} ({(installation.Plugin ? "plugin" : "standalone")})";

    private static void RenderHuman(ServicesStatusReport report)
    {
        if (report.ComposeVersion is not null)
        {
            AnsiConsole.MarkupLine($"Docker Compose: {report.ComposeVersion.EscapeMarkup()}");
        }

        if (report.Services.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No Squeezr services are running.[/]");
            return;
        }

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

        AnsiConsole.Write(table);
    }
}
