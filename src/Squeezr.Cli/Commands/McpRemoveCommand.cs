using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Cli.Output;

namespace Squeezr.Cli.Commands;

public sealed class McpRemoveCommand : AsyncCommand<McpRemoveCommand.Settings>
{
    private readonly ClientCliLocator _locator;

    public McpRemoveCommand()
    {
        _locator = new ClientCliLocator();
    }

    internal McpRemoveCommand(ClientCliLocator locator)
    {
        _locator = locator;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--client")]
        public string[]? Client { get; set; }

        [CommandOption("--scope")]
        public string? Scope { get; set; }

        [CommandOption("--project")]
        public bool Project { get; set; }

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

        if (settings.Client is not { Length: > 0 } && settings.Project)
        {
            return StripProjectBlockOnly(environment, settings.Json);
        }

        IReadOnlyList<McpClientDescriptor> clients;
        McpScope scope;
        try
        {
            clients = await McpCommandSupport.ResolveClientsAsync(settings.Client, interactive, environment, _locator, cancellationToken);
            scope = await McpCommandSupport.ResolveScopeAsync(settings.Scope, interactive, cancellationToken);
        }
        catch (McpUsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        var service = new McpOnboardingService(_locator);
        var results = new List<McpRemoveResult>();
        foreach (var client in clients)
        {
            try
            {
                results.Add(await service.RemoveAsync(client, scope, environment, cancellationToken));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                        ClientCliException or TimeoutException or InvalidOperationException or NotSupportedException)
            {
                results.Add(new McpRemoveResult(
                    client.Id.ToString().ToLowerInvariant(),
                    scope.ToString().ToLowerInvariant(),
                    "none",
                    McpClientPaths.ConfigPath(client, environment, scope),
                    false,
                    null,
                    ex.Message));
            }
        }

        IReadOnlyList<string>? stripped = null;
        if (settings.Project)
        {
            stripped = ProjectInstructionsWriter.Strip(environment.WorkspaceRoot);
        }

        if (settings.Json)
        {
            if (stripped is not null)
            {
                results.Add(new McpRemoveResult(
                    "project", "project", "writer", environment.WorkspaceRoot, stripped.Count > 0, null, null));
            }
            JsonOutput.Write(results);
        }
        else
        {
            if (McpCommandSupport.RequestsAllClients(settings.Client))
            {
                AnsiConsole.MarkupLine(
                    $"[grey]detected: {string.Join(", ", clients.Select(client => client.Id.ToString().ToLowerInvariant()))}[/]");
            }
            Render(results, stripped);
        }
        return results.Any(result => result.Error is not null) ? 1 : 0;
    }

    private static int StripProjectBlockOnly(McpOnboardingEnvironment environment, bool json)
    {
        var stripped = ProjectInstructionsWriter.Strip(environment.WorkspaceRoot);
        if (json)
        {
            JsonOutput.Write(new[]
            {
                new McpRemoveResult("project", "project", "writer", environment.WorkspaceRoot, stripped.Count > 0, null, null),
            });
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]✓[/] project block removed — {stripped.Count} file(s)");
            foreach (var path in stripped)
            {
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(path)}[/]");
            }
        }
        return 0;
    }

    private static void Render(IReadOnlyList<McpRemoveResult> results, IReadOnlyList<string>? stripped)
    {
        foreach (var result in results)
        {
            if (result.Error is { } error)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(result.Client)} ({Markup.Escape(result.Scope)}) — {Markup.Escape(error)}");
                continue;
            }
            var method = result.Method == "cli" ? "via client CLI" : "config file";
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(result.Client)} ({Markup.Escape(result.Scope)}) — removed {method}");
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(result.ConfigPath)}[/]");
            if (result.Warning is { } warning)
            {
                AnsiConsole.MarkupLine($"  [yellow]warning:[/] {Markup.Escape(warning)}");
            }
            if (result.InstructionsRemoved)
            {
                AnsiConsole.MarkupLine("  [green]-[/] instructions block removed");
            }
        }
        if (stripped is { Count: > 0 })
        {
            AnsiConsole.MarkupLine($"[green]-[/] project block removed — {stripped.Count} file(s)");
            foreach (var path in stripped)
            {
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(path)}[/]");
            }
        }
    }
}
