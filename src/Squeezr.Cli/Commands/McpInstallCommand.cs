using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Cli.Output;

namespace Squeezr.Cli.Commands;

public sealed class McpInstallCommand : AsyncCommand<McpInstallCommand.Settings>
{
    private readonly ClientCliLocator _locator;

    public McpInstallCommand()
    {
        _locator = new ClientCliLocator();
    }

    internal McpInstallCommand(ClientCliLocator locator)
    {
        _locator = locator;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--client")]
        public string[]? Client { get; set; }

        [CommandOption("--all")]
        public bool All { get; set; }

        [CommandOption("--scope")]
        public string? Scope { get; set; }

        [CommandOption("--no-watch")]
        public bool NoWatch { get; set; }

        [CommandOption("--no-instructions")]
        public bool NoInstructions { get; set; }

        [CommandOption("--instructions")]
        public bool Instructions { get; set; }

        [CommandOption("--project")]
        public bool Project { get; set; }

        [CommandOption("--yes")]
        public bool Yes { get; set; }

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

        var clientValues = settings.Client;
        if (settings.All)
        {
            clientValues = clientValues is { Length: > 0 } ? [.. clientValues, "all"] : ["all"];
        }

        if (clientValues is not { Length: > 0 } && settings.Project)
        {
            return WriteProjectBlockOnly(environment, settings.Json);
        }

        if (settings.Yes && clientValues is not { Length: > 0 })
        {
            Console.Error.WriteLine(
                $"error: --yes requires --client (<csv>|all). Specify at least one client with --client ({McpCommandSupport.ClientValues}), or --all to onboard every detected client.");
            return 1;
        }

        var scopeValue = settings.Scope;
        var scopeDefaultedToUser = false;
        if (scopeValue is null && settings.Yes)
        {
            scopeValue = "user";
            scopeDefaultedToUser = true;
        }

        IReadOnlyList<McpClientDescriptor> clients;
        McpScope scope;
        try
        {
            clients = await McpCommandSupport.ResolveClientsAsync(clientValues, interactive, environment, _locator, cancellationToken);
            scope = await McpCommandSupport.ResolveScopeAsync(scopeValue, interactive, cancellationToken);
        }
        catch (McpUsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        if (scopeDefaultedToUser && !settings.Json)
        {
            AnsiConsole.MarkupLine("[grey]scope: user (--yes default; pass --scope project to override)[/]");
        }

        var writeInstructions = await McpCommandSupport.ResolveWriteInstructionsAsync(
            settings.NoInstructions, settings.Instructions, settings.Yes, interactive, cancellationToken);
        if (!writeInstructions && !settings.NoInstructions && !settings.Yes && !interactive)
        {
            Console.Error.WriteLine("note: instructions block skipped (non-interactive); pass --yes --instructions to add it.");
        }

        var writeProjectBlock = await McpCommandSupport.ResolveProjectBlockAsync(
            settings.Project, settings.Yes, interactive, cancellationToken);

        var service = new McpOnboardingService(_locator);
        var results = new List<McpInstallResult>();
        foreach (var client in clients)
        {
            try
            {
                results.Add(await service.InstallAsync(
                    client, scope, !settings.NoWatch, writeInstructions, environment, cancellationToken));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                        ClientCliException or TimeoutException or InvalidOperationException or NotSupportedException)
            {
                results.Add(new McpInstallResult(
                    client.Id.ToString().ToLowerInvariant(),
                    scope.ToString().ToLowerInvariant(),
                    "none",
                    McpClientPaths.ConfigPath(client, environment, scope),
                    false,
                    null,
                    ex.Message));
            }
        }

        ProjectBlockChange? projectChange = null;
        if (writeProjectBlock)
        {
            projectChange = ProjectInstructionsWriter.Refresh(environment.WorkspaceRoot);
            WarnOrphans(projectChange.Orphaned);
        }

        if (settings.Json)
        {
            if (projectChange is not null)
            {
                results.Add(ProjectInstallResult(environment, projectChange));
            }
            JsonOutput.Write(results);
        }
        else
        {
            if (McpCommandSupport.RequestsAllClients(clientValues))
            {
                AnsiConsole.MarkupLine(
                    $"[grey]detected: {string.Join(", ", clients.Select(client => client.Id.ToString().ToLowerInvariant()))}[/]");
            }
            Render(results, projectChange);
        }
        return results.Any(result => result.Error is not null) ? 1 : 0;
    }

    private static int WriteProjectBlockOnly(McpOnboardingEnvironment environment, bool json)
    {
        var change = ProjectInstructionsWriter.Refresh(environment.WorkspaceRoot);
        WarnOrphans(change.Orphaned);
        if (json)
        {
            JsonOutput.Write(new[] { ProjectInstallResult(environment, change) });
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]✓[/] project block written — {change.Written.Count} file(s)");
            foreach (var path in change.Written)
            {
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(path)}[/]");
            }
        }
        return 0;
    }

    private static McpInstallResult ProjectInstallResult(McpOnboardingEnvironment environment, ProjectBlockChange change) =>
        new("project", "project", "writer", environment.WorkspaceRoot, change.Written.Count > 0,
            change.Orphaned.Count > 0 ? string.Join("; ", change.Orphaned) : null, null);

    private static void WarnOrphans(IReadOnlyList<string> orphaned)
    {
        foreach (var path in orphaned)
        {
            Console.Error.WriteLine(
                $"warning: {path}: one-sided squeezr:project-instructions markers; file left untouched");
        }
    }

    private static void Render(IReadOnlyList<McpInstallResult> results, ProjectBlockChange? projectChange)
    {
        foreach (var result in results)
        {
            if (result.Error is { } error)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(result.Client)} ({Markup.Escape(result.Scope)}) — {Markup.Escape(error)}");
                continue;
            }
            var method = result.Method == "cli" ? "via client CLI" : "config file";
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(result.Client)} ({Markup.Escape(result.Scope)}) — {method}");
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(result.ConfigPath)}[/]");
            if (result.Warning is { } warning)
            {
                AnsiConsole.MarkupLine($"  [yellow]warning:[/] {Markup.Escape(warning)}");
            }
            if (result.InstructionsWritten)
            {
                AnsiConsole.MarkupLine($"  [green]+[/] instructions block written");
            }
        }
        if (projectChange is not null)
        {
            AnsiConsole.MarkupLine($"[green]+[/] project block written — {projectChange.Written.Count} file(s)");
            foreach (var path in projectChange.Written)
            {
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(path)}[/]");
            }
        }
    }
}
