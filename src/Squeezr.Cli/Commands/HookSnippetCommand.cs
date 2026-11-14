using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Mcp.Onboarding;

namespace Squeezr.Cli.Commands;

public sealed class HookSnippetCommand : Command<HookSnippetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--client")]
        public string[]? Client { get; set; }

        [CommandOption("--session-start")]
        public bool SessionStart { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var environment = McpOnboardingEnvironment.Create(WorkspaceRootResolver.ResolveCurrentDirectory().RootPath);
        var rows = new List<JsonObject>();
        foreach (var client in Resolve(settings.Client))
        {
            if (McpClientPaths.HooksPath(client, environment, McpScope.User) is not { } hooksPath)
            {
                continue;
            }

            // Each event holds an ARRAY of matcher groups in the client's own layout — the writer appends into
            // one — so a pasted fragment has to arrive in that shape or the client reads it as a foreign form.
            var blocks = new JsonObject();
            if (HooksBlock.ForClient(client, environment.CommandPath) is { } preRead)
            {
                blocks[preRead.Event] = new JsonArray(preRead.ToJson());
            }

            if (settings.SessionStart && HooksBlock.ForSessionStart(client, environment.CommandPath) is { } start)
            {
                blocks[start.Event] = new JsonArray(start.ToJson());
            }

            if (blocks.Count == 0)
            {
                continue;
            }

            rows.Add(new JsonObject
            {
                ["client"] = client.Id.ToString().ToLowerInvariant(),
                ["path"] = hooksPath,
                ["fragment"] = new JsonObject { ["hooks"] = blocks },
            });
        }

        if (settings.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (rows.Count == 0)
        {
            Console.Error.WriteLine(
                $"no client with a verified hook layout — pass --client ({McpCommandSupport.ClientValues})");
            return 1;
        }

        foreach (var row in rows)
        {
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(row["client"]!.GetValue<string>())}[/] — merge into {Markup.Escape(row["path"]!.GetValue<string>())}");
            AnsiConsole.WriteLine(row["fragment"]!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine(
            "[grey]squeezr mcp install --session-start writes this block for you; this command only prints it.[/]");
        return 0;
    }

    // The clients whose hook layout squeezr has verified — a client with no resolver has no path to name, and
    // printing an invented one is exactly the thing this surface must not do.
    private static IReadOnlyList<McpClientDescriptor> Resolve(string[]? values)
    {
        if (values is not { Length: > 0 })
        {
            return McpClientRegistry.All
                .Where(client => client.HooksPathResolver is not null)
                .ToList();
        }

        var selected = new List<McpClientDescriptor>();
        foreach (var value in values)
        {
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    return McpClientRegistry.All.Where(client => client.HooksPathResolver is not null).ToList();
                }

                if (McpClientRegistry.TryGet(part, out var client) && client.HooksPathResolver is not null)
                {
                    selected.Add(client);
                }
            }
        }

        return selected;
    }
}
