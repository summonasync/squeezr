using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class McpDoctorCommand : AsyncCommand<McpDoctorCommand.Settings>
{
    private readonly ClientCliLocator _locator;

    public McpDoctorCommand()
    {
        _locator = new ClientCliLocator();
    }

    internal McpDoctorCommand(ClientCliLocator locator)
    {
        _locator = locator;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--client")]
        public string[]? Client { get; set; }

        [CommandOption("--all")]
        public bool All { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;
        McpOnboardingEnvironment environment;
        try
        {
            environment = McpOnboardingEnvironment.Create(
                WorkspaceRootResolver.ResolveCurrentDirectory().RootPath,
                WorkspaceRootResolver.LaunchDirectory());
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        IReadOnlyList<McpClientDescriptor> clients;
        try
        {
            var clientValues = settings.Client;
            if (settings.All)
            {
                clientValues = clientValues is { Length: > 0 } ? [.. clientValues, "all"] : ["all"];
            }
            clients = await McpCommandSupport.ResolveClientsAsync(clientValues, interactive, environment, _locator, cancellationToken);
        }
        catch (McpUsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        var service = new McpOnboardingService(_locator);
        var diagnostics = new List<McpClientDiagnostics>();
        foreach (var client in clients)
        {
            diagnostics.Add(await service.DoctorAsync(client, environment, cancellationToken));
        }

        if (settings.Json)
        {
            JsonOutput.Write(diagnostics);
        }
        else
        {
            Render(diagnostics);
        }
        return diagnostics.All(entry => entry.UserScopeEntry || entry.ProjectScopeEntry) ? 0 : 1;
    }

    internal static void Render(IReadOnlyList<McpClientDiagnostics> diagnostics)
    {
        foreach (var entry in diagnostics)
        {
            var installed = entry.Installed ? "[green]installed[/]" : "[grey]not installed[/]";
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(entry.Client)}[/] — {installed}");
            AnsiConsole.MarkupLine(
                $"  entry: user={Flag(entry.UserScopeEntry)} project={Flag(entry.ProjectScopeEntry)}" +
                $"   would write via: user={Markup.Escape(entry.UserScopeMethod)}, project={Markup.Escape(entry.ProjectScopeMethod)}");
            AnsiConsole.MarkupLine(
                $"  squeezr in PATH: {Flag(entry.SqueezrInPath)}   workspace indexed: {Flag(entry.WorkspaceIndexed)}" +
                $"   headless: {Markup.Escape(entry.HeadlessStatus ?? "n/a")}");
            RenderIndexRemedy(entry);
            RenderStale(entry);
            foreach (var warning in entry.Warnings)
            {
                AnsiConsole.MarkupLine($"  [yellow]warning:[/] {Markup.Escape(warning)}");
            }
        }

        RenderWatch(diagnostics);
    }

    // The store the registration lives on, as the watch claim answers it: who runs the indexer, who merely
    // observes, and — since 070/T5 stopped stamping --watch into every client — the exact command that starts
    // a watcher where none runs. One block per doctor run: the store is the same under every client entry.
    private static void RenderWatch(IReadOnlyList<McpClientDiagnostics> diagnostics)
    {
        var watch = diagnostics.FirstOrDefault(entry => entry.Watch is not null)?.Watch;
        if (watch is null)
        {
            return;
        }

        var label = Markup.Escape($"'{watch.Name}'");
        var observers = watch.Observers > 0 ? $" — {watch.Observers} observer(s)" : string.Empty;
        switch (watch.OwnerState)
        {
            case "alive":
                AnsiConsole.MarkupLine(
                    $"[bold]watch[/] {label}: owner pid {watch.OwnerPid} ({Markup.Escape(watch.OwnerKind ?? "")}){observers}");
                break;
            case "foreign":
                AnsiConsole.MarkupLine(
                    $"[bold]watch[/] {label}: indexer runs on host '{Markup.Escape(watch.OwnerHost ?? "")}' (pid {watch.OwnerPid})");
                break;
            case "wedged":
                AnsiConsole.MarkupLine(
                    $"[bold]watch[/] {label}: [yellow]wedged[/] — pid {watch.OwnerPid} holds the claim with a stale beat and runs no passes{observers}");
                RenderStart(watch);
                break;
            case "gone":
                AnsiConsole.MarkupLine(
                    $"[bold]watch[/] {label}: [yellow]no indexer[/] — the last claim named pid {watch.OwnerPid}, which is gone{observers}");
                RenderStart(watch);
                break;
            default:
                AnsiConsole.MarkupLine(
                    $"[bold]watch[/] {label}: [yellow]no background indexer[/] — nothing owns the watch loop on this store{observers}");
                RenderStart(watch);
                break;
        }
    }

    private static void RenderStart(StoreWatchDiagnostics watch)
    {
        if (watch.StartCommand is { } command)
        {
            AnsiConsole.MarkupLine($"  [yellow]start:[/] {Markup.Escape(command)}");
        }
    }

    private static void RenderIndexRemedy(McpClientDiagnostics entry)
    {
        if (entry.IndexState?.NextSteps is not { Count: > 0 } steps)
        {
            return;
        }

        AnsiConsole.MarkupLine($"  [yellow]remedy:[/] {Markup.Escape(string.Join(" && ", steps))}");
    }

    private static void RenderStale(McpClientDiagnostics entry)
    {
        if (!entry.UserStale && !entry.ProjectStale)
        {
            return;
        }
        var expected = entry.ExpectedCommand ?? "";
        if (entry.UserStale)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]stale (user):[/] registered={Markup.Escape(entry.UserCommand ?? "")} → expected={Markup.Escape(expected)}");
            AnsiConsole.MarkupLine($"  [yellow]repair:[/] {Markup.Escape(RemedyText.McpInstallRepair(entry.Client))}");
        }
        if (entry.ProjectStale)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]stale (project):[/] registered={Markup.Escape(entry.ProjectCommand ?? "")} → expected={Markup.Escape(expected)}");
            AnsiConsole.MarkupLine($"  [yellow]repair:[/] {Markup.Escape(RemedyText.McpInstallRepair(entry.Client))}");
        }
    }

    private static string Flag(bool value) => value ? "[green]yes[/]" : "[red]no[/]";
}
