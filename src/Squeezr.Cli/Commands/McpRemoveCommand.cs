using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;

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
        WorkspaceResolution resolution;
        try
        {
            resolution = WorkspaceRootResolver.ResolveCurrentDirectory();
            environment = McpOnboardingEnvironment.Create(resolution.RootPath);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        if (settings.Client is not { Length: > 0 } && settings.Project)
        {
            // Stripped from the same checkout install wrote to: a lane that carries its own block must be able
            // to lose it by running the verb where the block lives, not only where the parent store was made.
            return StripProjectBlockOnly(
                ProjectCheckout.From(WorkspaceRootResolver.LaunchDirectory(), resolution), settings.Json);
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

        ProjectCheckout? checkout = settings.Project
            ? ProjectCheckout.From(WorkspaceRootResolver.LaunchDirectory(), resolution)
            : null;
        IReadOnlyList<string>? stripped = checkout is null
            ? null
            : ProjectInstructionsWriter.Strip(checkout.Root);

        if (settings.Json)
        {
            if (checkout is not null && stripped is not null)
            {
                results.Add(new McpRemoveResult(
                    "project", "project", "writer", checkout.Root, stripped.Count > 0, null, null));
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

    private static int StripProjectBlockOnly(ProjectCheckout checkout, bool json)
    {
        var stripped = ProjectInstructionsWriter.Strip(checkout.Root);
        if (json)
        {
            JsonOutput.Write(new[]
            {
                new McpRemoveResult("project", "project", "writer", checkout.Root, stripped.Count > 0, null, null),
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
            if (result.HooksRemoved)
            {
                AnsiConsole.MarkupLine("  [green]-[/] hook pre-read removed");
            }
            if (result.SessionStartRemoved)
            {
                AnsiConsole.MarkupLine("  [green]-[/] session-start hook removed");
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
