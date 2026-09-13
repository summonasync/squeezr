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

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive;
        McpOnboardingEnvironment environment;
        try
        {
            environment = McpOnboardingEnvironment.Create(Directory.GetCurrentDirectory());
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
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

        if (settings.Json)
        {
            JsonOutput.Write(results);
        }
        else
        {
            if (McpCommandSupport.RequestsAllClients(settings.Client))
            {
                AnsiConsole.MarkupLine(
                    $"[grey]detected: {string.Join(", ", clients.Select(client => client.Id.ToString().ToLowerInvariant()))}[/]");
            }
            Render(results);
        }
        return results.Any(result => result.Error is not null) ? 1 : 0;
    }

    private static void Render(IReadOnlyList<McpRemoveResult> results)
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
    }
}
