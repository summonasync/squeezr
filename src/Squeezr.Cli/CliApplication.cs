using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Tui;
using System.Reflection;

namespace Squeezr.Cli;

public static class CliApplication
{
    public static Task<int> RunAsync(string[] args, IAnsiConsole? console = null, bool? interactive = null)
    {
        if (args.Length == 0 && (interactive ?? AnsiConsole.Profile.Capabilities.Interactive))
        {
            return console is not null
                ? TuiApp.RunAsync(console, Directory.GetCurrentDirectory())
                : TuiApp.RunInteractiveAsync(Directory.GetCurrentDirectory());
        }

        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("squeezr");
            config.SetApplicationVersion(typeof(CliApplication).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
            config.AddCommand<InitCommand>("init")
                .WithDescription("Create a new Squeezr workspace in the current directory");
            config.AddCommand<AddCommand>("add")
                .WithDescription("Add a repository to the workspace");
            config.AddCommand<StatusCommand>("status")
                .WithDescription("Show workspace and index status");
            config.AddCommand<IndexCommand>("index")
                .WithDescription("Index workspace repositories into the trigram index");
            config.AddCommand<SearchCommand>("search")
                .WithDescription("Exact substring or regex search over the trigram index");
            config.AddCommand<SymbolCommand>("symbol")
                .WithDescription("Exact symbol lookup: signature, doc-comment, file:span");
            config.AddCommand<RefsCommand>("refs")
                .WithDescription("Caller-side reference points for a symbol: file:line kind");
            config.AddCommand<MapCommand>("map")
                .WithDescription("Repository map: directory tree with symbol signatures, no bodies");
            config.AddCommand<ExpandCommand>("expand")
                .WithDescription("Expand a line span from a file with surrounding context");
            config.AddCommand<CompactCommand>("compact")
                .WithDescription("Rewrite the trigram index dropping tombstoned segments");
            config.AddCommand<DropCommand>("drop")
                .WithDescription("Remove the .squeezr index store");
            config.AddCommand<WatchCommand>("watch")
                .WithDescription("Watch the workspace and re-index on file changes");
            config.AddCommand<ServeCommand>("serve")
                .WithDescription("Run an MCP server over stdio for AI agents");
            config.AddCommand<TuiCommand>("tui")
                .WithDescription("Interactive terminal UI (dashboard, search, wizards)");
            McpCommandRegistration.AddMcpBranch(config);
        });

        return app.RunAsync(args);
    }
}
