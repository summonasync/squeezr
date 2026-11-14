using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;

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

        [CommandOption("--watch")]
        public bool Watch { get; set; }

        // Accepted for compatibility: it became the default when registrations stopped breeding a watcher per
        // client session (070/T5), so pasting an old --no-watch command still does exactly what it says.
        [CommandOption("--no-watch")]
        public bool NoWatch { get; set; }

        [CommandOption("--no-instructions")]
        public bool NoInstructions { get; set; }

        [CommandOption("--instructions")]
        public bool Instructions { get; set; }

        [CommandOption("--project")]
        public bool Project { get; set; }

        // The only hook arm here that is not automatic: it puts a command into the user's client settings that
        // runs at every session start, so --yes does NOT imply it and a terminal answers the prompt with 'no'.
        [CommandOption("--session-start")]
        public bool SessionStart { get; set; }

        [CommandOption("--yes")]
        public bool Yes { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Watch && settings.NoWatch)
        {
            Console.Error.WriteLine("error: --watch and --no-watch cannot be combined; --no-watch is the default.");
            return 1;
        }

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

        var clientValues = settings.Client;
        if (settings.All)
        {
            clientValues = clientValues is { Length: > 0 } ? [.. clientValues, "all"] : ["all"];
        }

        if (clientValues is not { Length: > 0 } && settings.Project)
        {
            if (settings.SessionStart)
            {
                Console.Error.WriteLine(
                    "error: --session-start arms a client hook and this path arms none — pass --client (<csv>|all) " +
                    "with --session-start, or drop --session-start to write only the project block.");
                return 1;
            }

            return WriteProjectBlockOnly(
                ProjectCheckout.From(WorkspaceRootResolver.LaunchDirectory(), resolution), settings.Json);
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
        // No prompt here, deliberately: the interactive install flow is a shipped contract of its own (three
        // prompts, pinned byte-for-byte), and an arm into the user's client settings is the one act that should
        // not be reachable by whatever the terminal's next Enter happens to select. The flag is the consent.
        var sessionStart = settings.SessionStart;
        if (!sessionStart && clients.Any(client => HooksBlock.ForSessionStart(client, environment.CommandPath) is not null))
        {
            Console.Error.WriteLine(
                "note: session-start hook not armed (opt-in) — 'squeezr hook snippet --session-start' prints the " +
                "block, 'squeezr mcp install --session-start' writes it.");
        }

        var service = new McpOnboardingService(_locator);
        var results = new List<McpInstallResult>();
        foreach (var client in clients)
        {
            try
            {
                results.Add(await service.InstallAsync(
                    client, scope, settings.Watch, writeInstructions, sessionStart, environment, cancellationToken));
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

        ProjectCheckout? checkout = writeProjectBlock
            ? ProjectCheckout.From(WorkspaceRootResolver.LaunchDirectory(), resolution)
            : null;
        ProjectBlockChange? projectChange = checkout is null
            ? null
            : ProjectInstructionsWriter.Refresh(checkout.Root, checkout.Block);
        if (projectChange is not null)
        {
            WarnOrphans(projectChange.Orphaned);
        }

        if (settings.Json)
        {
            if (checkout is not null && projectChange is not null)
            {
                results.Add(ProjectInstallResult(checkout, projectChange));
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

    private static int WriteProjectBlockOnly(ProjectCheckout checkout, bool json)
    {
        var change = ProjectInstructionsWriter.Refresh(checkout.Root, checkout.Block);
        WarnOrphans(change.Orphaned);
        if (json)
        {
            JsonOutput.Write(new[] { ProjectInstallResult(checkout, change) });
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

    private static McpInstallResult ProjectInstallResult(ProjectCheckout checkout, ProjectBlockChange change) =>
        new("project", "project", "writer", checkout.Root, change.Written.Count > 0,
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
            if (result.HooksArmed)
            {
                AnsiConsole.MarkupLine("  [green]+[/] hook pre-read armed");
            }
            if (result.PostEditHooksArmed)
            {
                AnsiConsole.MarkupLine("  [green]+[/] hook post-edit armed");
            }

            if (result.SessionStart is { } sessionStart)
            {
                AnsiConsole.MarkupLine($"  [green]+[/] session-start hook armed — {Markup.Escape(sessionStart.Path)}");
                foreach (var line in sessionStart.Block.Split('\n', StringSplitOptions.TrimEntries))
                {
                    if (line.Length > 0)
                    {
                        AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(line)}[/]");
                    }
                }
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
