using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Cli.Output;

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
                WorkspaceRootResolver.ResolveCurrentDirectory().RootPath);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        IReadOnlyList<McpClientDescriptor> clients;
        try
        {
            clients = await McpCommandSupport.ResolveClientsAsync(settings.Client, interactive, environment, _locator, cancellationToken);
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

    private static void Render(IReadOnlyList<McpClientDiagnostics> diagnostics)
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
            foreach (var warning in entry.Warnings)
            {
                AnsiConsole.MarkupLine($"  [yellow]warning:[/] {Markup.Escape(warning)}");
            }
        }
    }

    private static string Flag(bool value) => value ? "[green]yes[/]" : "[red]no[/]";
}
