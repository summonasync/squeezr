using Spectre.Console.Cli;
using Squeezr.Cli.Commands;

namespace Squeezr.Cli;

public static class CliApplication
{
    public static Task<int> RunAsync(string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("squeezr");
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
            config.AddCommand<MapCommand>("map")
                .WithDescription("Repository map: directory tree with symbol signatures, no bodies");
            config.AddCommand<ExpandCommand>("expand")
                .WithDescription("Expand a line span from a file with surrounding context");
            config.AddCommand<CompactCommand>("compact")
                .WithDescription("Rewrite the trigram index dropping tombstoned segments");
            config.AddCommand<DropCommand>("drop")
                .WithDescription("Remove the .squeezr index store");
        });

        return app.RunAsync(args);
    }
}
