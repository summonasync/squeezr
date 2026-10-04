using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Output;
using Squeezr.Cli.Tui;

namespace Squeezr.Cli;

public static class CliApplication
{
    public static Task<int> RunAsync(string[] args, IAnsiConsole? console = null, bool? interactive = null)
    {
        if (args.Length == 0 && (interactive ?? AnsiConsole.Profile.Capabilities.Interactive))
        {
            var workspaceRoot = WorkspaceRootResolver.ResolveCurrentDirectory().RootPath;
            return console is not null
                ? TuiApp.RunAsync(console, workspaceRoot)
                : TuiApp.RunInteractiveAsync(workspaceRoot);
        }

        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("squeezr");
            config.SetApplicationVersion(AppVersion.Current);
            config.AddCommand<InitCommand>("init")
                .WithDescription("Create a new Squeezr workspace in the current directory");
            config.AddCommand<AddCommand>("add")
                .WithDescription("Add a repository to the workspace");
            config.AddCommand<StatusCommand>("status")
                .WithDescription("Show workspace and index status");
            config.AddCommand<StatsCommand>("stats")
                .WithDescription("Show query telemetry: per-tool stats, token savings (whole-file baseline of expanded files vs actual tokens), cache hit-rate");
            config.AddCommand<UsageCommand>("usage")
                .WithDescription("Show query telemetry rollup across all registered workspaces");
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
                .WithDescription("Remove the workspace index store (use --store-key for stale entries)");
            config.AddCommand<WatchCommand>("watch")
                .WithDescription("Watch the workspace and re-index on file changes");
            config.AddCommand<ServeCommand>("serve")
                .WithDescription("Run an MCP server over stdio for AI agents");
            config.AddCommand<TuiCommand>("tui")
                .WithDescription("Interactive terminal UI (dashboard, search, wizards)");
            config.AddBranch("fleet", fleet =>
            {
                fleet.SetDescription("Show and manage watch/serve-watch daemons across all registered workspaces");
                fleet.SetDefaultCommand<FleetCommand>();
                fleet.AddCommand<FleetStopCommand>("stop")
                    .WithDescription("Stop the watch and serve-watch daemons of a workspace (exact store key)");
                fleet.AddCommand<FleetRestartCommand>("restart")
                    .WithDescription("Restart the watch daemon of a workspace (exact store key)");
            });
            config.AddBranch("services", services =>
            {
                services.SetDescription("Manage Squeezr infrastructure services (Qdrant and Ollama)");
                services.AddCommand<ServicesUpCommand>("up")
                    .WithDescription("Generate the compose file and start Qdrant and Ollama");
                services.AddCommand<ServicesDownCommand>("down")
                    .WithDescription("Stop Qdrant and Ollama (data volumes are kept)");
                services.AddCommand<ServicesStatusCommand>("status")
                    .WithDescription("Show the state of the Qdrant and Ollama containers");
            });
            config.AddBranch("workspace", workspace =>
            {
                workspace.SetDescription("Inspect the current workspace");
                workspace.AddCommand<WorkspaceListCommand>("list")
                    .WithDescription("List registered workspaces");
                workspace.AddCommand<WorkspaceRepairCommand>("repair")
                    .WithDescription("Adopt orphaned workspace stores back into the registry (never deletes anything)");
            });
            config.AddBranch("vectors", vectors =>
            {
                vectors.SetDescription("Keep the Qdrant collection in step with the index");
                vectors.AddCommand<VectorsReconcileCommand>("reconcile")
                    .WithDescription(
                        "Drop collection points no live chunk hash maps to and re-queue chunks whose vector is " +
                        "gone — dry run unless --apply is given");
            });
            config.AddBranch("hook", hook =>
            {
                hook.SetDescription("Produce payloads for AI client hooks (advisory, never blocks a tool call)");
                hook.AddCommand<HookPreReadCommand>("pre-read")
                    .WithDescription("PreToolUse nudge for Read: point large reads at the indexed navigation tools");
            });
            McpCommandRegistration.AddMcpBranch(config);
        });

        RedirectedOutput.ApplyLayout(AnsiConsole.Console, System.Console.IsOutputRedirected);
        return app.RunAsync(args);
    }
}
