using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Lite.Commands;
using Squeezr.Lite.Output;

namespace Squeezr.Lite;

public static class LiteApplication
{
    public static Task<int> RunAsync(string[] args)
    {
        if (Console.IsOutputRedirected)
        {
            Console.OutputEncoding = Encoding.UTF8;
        }

        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("sqzrlite");
            config.SetApplicationVersion(AppVersion.Current);
            config.AddCommand<InitCommand>("init")
                .WithDescription("Create a new sqzrlite workspace in the current directory");
            config.AddCommand<AddCommand>("add")
                .WithDescription("Add a repository to the workspace");
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
            config.AddCommand<SpanCommand>("span")
                .WithDescription("Expand a line span from a file with surrounding context");
            config.AddCommand<PackCommand>("pack")
                .WithDescription("One answer per symbol: definition, body within budget, every caller");
            config.AddCommand<StatusCommand>("status")
                .WithDescription("Show workspace and index status");
            config.AddCommand<ServeCommand>("serve")
                .WithDescription("Run an MCP server over stdio for AI agents");
        });

        RedirectedOutput.ApplyLayout(AnsiConsole.Console, System.Console.IsOutputRedirected);
        return app.RunAsync(args);
    }
}
