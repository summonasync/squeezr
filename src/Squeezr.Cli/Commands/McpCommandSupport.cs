using Spectre.Console;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Cli.Commands;

public static class McpCommandSupport
{
    private const string ClientValues = "claude, kimi, qwen, codex, all";

    public static async Task<IReadOnlyList<McpClientDescriptor>> ResolveClientsAsync(
        string[]? values,
        bool interactive,
        McpOnboardingEnvironment environment,
        ClientCliLocator locator,
        CancellationToken cancellationToken)
    {
        if (values is { Length: > 0 })
        {
            return ParseClients(values, environment, locator);
        }
        if (!interactive)
        {
            throw new McpUsageException(
                $"Specify at least one client with --client ({ClientValues}), or --all to onboard every detected client. " +
                "Scope and client prompts require an interactive terminal.");
        }
        var detected = McpClientRegistry.All
            .Where(client => ClientDetector.IsInstalled(client, environment, locator))
            .ToList();
        if (detected.Count == 0)
        {
            throw new McpUsageException(
                $"No supported MCP clients were detected; pass --client ({ClientValues}) to choose explicitly.");
        }
        var prompt = new MultiSelectionPrompt<McpClientDescriptor>()
            .Title("Select the MCP clients to onboard")
            .PageSize(10)
            .UseConverter(client => client.DisplayName)
            .AddChoices(detected);
        var selected = await prompt.ShowAsync(AnsiConsole.Console, cancellationToken);
        if (selected.Count == 0)
        {
            throw new McpUsageException("No clients selected.");
        }
        return selected;
    }

    public static async Task<McpScope> ResolveScopeAsync(string? value, bool interactive, CancellationToken cancellationToken)
    {
        if (value is not null)
        {
            if (Enum.TryParse<McpScope>(value, ignoreCase: true, out var parsed))
            {
                return parsed;
            }
            throw new McpUsageException($"Invalid --scope '{value}'; expected 'user' or 'project'.");
        }
        if (!interactive)
        {
            throw new McpUsageException(
                "Specify the scope with --scope user|project. " +
                "The scope prompt requires an interactive terminal.");
        }
        var prompt = new SelectionPrompt<McpScope>()
            .Title("Install scope")
            .UseConverter(ScopeLabel)
            .AddChoices(McpScope.User, McpScope.Project);
        return await prompt.ShowAsync(AnsiConsole.Console, cancellationToken);
    }

    public static async Task<bool> ResolveWriteInstructionsAsync(bool noInstructions, bool yes, bool interactive, CancellationToken cancellationToken)
    {
        if (noInstructions)
        {
            return false;
        }
        if (yes)
        {
            return true;
        }
        if (!interactive)
        {
            return false;
        }
        var prompt = new ConfirmationPrompt("Write the squeezr instructions block to the agent instruction files?")
        {
            DefaultValue = true,
        };
        return await prompt.ShowAsync(AnsiConsole.Console, cancellationToken);
    }

    public static bool RequestsAllClients(string[]? values) =>
        values is not null &&
        values.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(part => part.Equals("all", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<McpClientDescriptor> ParseClients(string[] values, McpOnboardingEnvironment environment, ClientCliLocator locator)
    {
        var selected = new List<McpClientDescriptor>();
        var wantsAll = false;
        foreach (var value in values)
        {
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    wantsAll = true;
                    continue;
                }
                if (!McpClientRegistry.TryGet(part, out var descriptor))
                {
                    throw new McpUsageException($"Unknown client '{part}'; expected one of: {ClientValues}.");
                }
                AddOnce(selected, descriptor);
            }
        }
        if (wantsAll)
        {
            var detected = McpClientRegistry.All
                .Where(client => ClientDetector.IsInstalled(client, environment, locator))
                .ToList();
            if (detected.Count == 0)
            {
                throw new McpUsageException(
                    "No agent CLIs were detected in PATH or config folders; " +
                    "'all' requires at least one detected client. Pass an explicit --client instead.");
            }
            foreach (var client in detected)
            {
                AddOnce(selected, client);
            }
        }
        return selected;
    }

    private static void AddOnce(List<McpClientDescriptor> selected, McpClientDescriptor client)
    {
        if (!selected.Any(existing => existing.Id == client.Id))
        {
            selected.Add(client);
        }
    }

    private static string ScopeLabel(McpScope scope) =>
        scope == McpScope.User
            ? "user (shared across all projects)"
            : "project (this workspace only)";
}
