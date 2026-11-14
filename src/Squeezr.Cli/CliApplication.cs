using System.Text;
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
        // redirected stdout inherits the host shell's legacy codepage (Git Bash pipes, CI logs),
        // where box-drawing and em-dashes mangle; an interactive terminal manages its own
        // encoding and stays untouched
        if (Console.IsOutputRedirected)
        {
            Console.OutputEncoding = Encoding.UTF8;
        }

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
            config.AddCommand<RankEvalCommand>("rank-eval")
                .WithDescription("Score ranking quality: run a golden query set through hybrid search, report MRR@10/P@5/Recall@10");
            config.AddCommand<DoctorCommand>("doctor")
                .WithDescription("One-point diagnosis: workspaces, services, daemons, orphans — findings with remedy hints, never repairs");
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
            config.AddBranch("watchd", watchd =>
            {
                watchd.SetDescription(
                    "Supervise watch daemons of all registered workspaces: keep desired watches up, " +
                    "restart after crashes and upgrades (singleton on this machine)");
                watchd.SetDefaultCommand<WatchdCommand>();
                watchd.AddCommand<WatchdInstallCommand>("install")
                    .WithDescription("Register watchd to start with this account and keep it running (idempotent)");
                watchd.AddCommand<WatchdUninstallCommand>("uninstall")
                    .WithDescription("Remove the autostart entry squeezr manages; a no-op when there is none");
                watchd.AddCommand<WatchdInstalledCommand>("installed")
                    .WithDescription(
                        "Report the autostart entry as the platform has it: registered or not, where, " +
                        "and whether the program it starts still exists (read-only, exit 0 only when it is healthy)");
            });
            config.AddCommand<ServeCommand>("serve")
                .WithDescription("Run an MCP server over stdio for AI agents");
            config.AddCommand<TuiCommand>("tui")
                .WithDescription("Interactive terminal UI (dashboard, search, wizards)");
            config.AddBranch("fleet", fleet =>
            {
                fleet.SetDescription("Show and manage watch/serve-watch daemons across all registered workspaces");
                fleet.SetDefaultCommand<FleetCommand>();
                fleet.AddCommand<FleetStartCommand>("start")
                    .WithDescription("Record desired=running for a workspace; watchd brings the watch daemon up (exact store key)");
                fleet.AddCommand<FleetStopCommand>("stop")
                    .WithDescription("Stop the watch and serve-watch daemons of a workspace and record desired=stopped (exact store key)");
                fleet.AddCommand<FleetRestartCommand>("restart")
                    .WithDescription("Restart the watch daemon of a workspace, clearing its crash-loop wedge (exact store key)");
                fleet.AddCommand<FleetStatusCommand>("status")
                    .WithDescription("Show supervision posture per workspace: desired state, supervised/wedged/managed elsewhere, last pass");
            });
            config.AddBranch("worktree", worktree =>
            {
                worktree.SetDescription("Give a git worktree its own store, filled from the indexed main worktree");
                worktree.SetDefaultCommand<WorktreeInitCommand>();
                worktree.AddCommand<WorktreeInitCommand>("init")
                    .WithDescription(
                        "Register this worktree as its own store — idempotent by path, and only once the main " +
                        "worktree is indexed");
                worktree.AddCommand<WorktreeDropCommand>("drop")
                    .WithDescription(
                        "Remove the store of this worktree: registry rows of every spelling of one root, their " +
                        "store dirs and their Qdrant collections");
                worktree.AddCommand<WorktreeResolveCommand>("resolve")
                    .WithDescription(
                        "Answer what store a path maps to and what that store says about itself — reads git's " +
                        "files, the registry and the store manifests, and writes nothing this verb decided: a " +
                        "registry file that is missing or unreadable is rebuilt from those manifests first");
                worktree.AddCommand<WorktreeListCommand>("list")
                    .WithDescription(
                        "Show one family: the parent checkout's row and the worktree rows that name it, with the " +
                        "branch and kind the registry recorded");
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
                    .WithDescription("Adopt orphaned workspace stores back into the registry, prune stale rows after confirmation (--dry-run lists, --yes confirms)");
                workspace.AddCommand<WorkspaceMigrateCommand>("migrate")
                    .WithDescription("Relocate a legacy in-project store into the central store on purpose; the move reports source, destination and file count (--yes confirms)");
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
                    .WithDescription("PreToolUse nudge on read-tool calls: point large reads at the indexed navigation tools");
                hook.AddCommand<HookPostEditCommand>("post-edit")
                    .WithDescription("PostToolUse path-scoped reindex after edit-tool calls: silent when a live watcher owns the store");
                hook.AddCommand<HookSessionStartCommand>("session-start")
                    .WithDescription(
                        "SessionStart hook body: in a linked git worktree, call 'worktree init' and tell the session " +
                        "which store answers for this branch. Creates nothing outside a linked checkout; never blocks a session");
                hook.AddCommand<HookSnippetCommand>("snippet")
                    .WithDescription("Print the paste-ready managed hook blocks for each client — writes nothing; 'mcp install --session-start' is the opt-in writer");
            });
            McpCommandRegistration.AddMcpBranch(config);
        });

        RedirectedOutput.ApplyLayout(AnsiConsole.Console, System.Console.IsOutputRedirected);
        return app.RunAsync(args);
    }
}
