using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class UsageCommand : Command<UsageCommand.Settings>
{
    private const int RetentionHintThreshold = 50_000;
    private const int DefaultWatchIntervalSeconds = 3;

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--agent <NAME>")]
        public string? Agent { get; set; }

        [CommandOption("--workspace <STORE_KEY>")]
        public string? Workspace { get; set; }

        [CommandOption("--since <PERIOD>")]
        public string? Since { get; set; }

        [CommandOption("--watch")]
        public bool Watch { get; set; }

        [CommandOption("--interval <SECONDS>")]
        public string? Interval { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (settings.Watch && settings.Json)
            {
                throw new McpUsageException(
                    "--watch cannot be combined with --json; live output is not available in json mode.");
            }

            var since = ParseSince(settings.Since);
            var interval = ParseInterval(settings.Interval);

            if (settings.Watch)
            {
                return Watch(settings, since, interval, cancellationToken);
            }

            var rollup = ComputeRollup(settings.Agent, settings.Workspace, since);
            if (settings.Json)
            {
                JsonOutput.Write(rollup);
            }
            else
            {
                Render(AnsiConsole.Console, rollup);
            }

            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or McpUsageException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static int Watch(Settings settings, DateTime? since, int intervalSeconds, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onStop = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onStop;
        try
        {
            if (!AnsiConsole.Profile.Capabilities.Interactive)
            {
                Render(AnsiConsole.Console, ComputeRollup(settings.Agent, settings.Workspace, since));
                Console.Error.WriteLine(
                    "warning: --watch requires an interactive terminal; printed a single snapshot instead.");
                return 0;
            }

            var interval = TimeSpan.FromSeconds(intervalSeconds);
            var view = new UsageLiveView(() => ComputeRollup(settings.Agent, settings.Workspace, since), interval);
            AnsiConsole.Live(view.Render()).Start(live =>
            {
                view.Bind(live);
                while (!cts.Token.WaitHandle.WaitOne(interval))
                {
                    view.Recompute();
                }
            });

            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= onStop;
        }
    }

    private static UsageRollup ComputeRollup(string? agent, string? workspace, DateTime? since) =>
        WorkspaceRootResolver.GuardRegistryIo(() => UsageRollupLoader.Load(agent, workspace, since));

    private static DateTime? ParseSince(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0)
        {
            throw InvalidSince(value);
        }

        if (!int.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
            amount <= 0)
        {
            throw InvalidSince(value);
        }

        try
        {
            var period = value[^1] switch
            {
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => throw InvalidSince(value),
            };
            return DateTime.UtcNow - period;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException or ArgumentException)
        {
            throw InvalidSince(value);
        }
    }

    private static int ParseInterval(string? value)
    {
        if (value is null)
        {
            return DefaultWatchIntervalSeconds;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
        {
            throw new McpUsageException(
                $"Invalid --interval '{value}'; expected a positive number of seconds.");
        }

        return seconds;
    }

    private static McpUsageException InvalidSince(string value) =>
        new($"Invalid --since '{value}'; expected a period like 90m, 24h, 7d or 30d.");

    public static void Render(IAnsiConsole console, UsageRollup rollup)
    {
        if (rollup.Totals.Records == 0)
        {
            console.MarkupLine("No usage telemetry yet.");
            return;
        }

        foreach (var workspace in rollup.Workspaces)
        {
            var stale = workspace.Stale ? " [yellow](stale)[/]" : string.Empty;
            console.MarkupLine(
                $"[bold]{workspace.StoreKey.EscapeMarkup()}[/]{stale} — {workspace.Records} record(s)");
            console.MarkupLine($"  [grey]{workspace.RootPath.EscapeMarkup()}[/]");
        }

        console.Write(BuildAgentTable(rollup));

        console.MarkupLine(
            $"Period: {rollup.Period.From:yyyy-MM-dd HH:mm} — {rollup.Period.To:yyyy-MM-dd HH:mm} " +
            $"({rollup.Totals.Records} records)");

        if (rollup.Totals.Sessions is { } adoption)
        {
            console.MarkupLine($"[grey]{AdoptionLine(adoption).EscapeMarkup()}[/]");
        }

        if (rollup.Totals.Records > RetentionHintThreshold)
        {
            console.MarkupLine(
                $"[grey]Telemetry holds {rollup.Totals.Records} records since {rollup.Period.From:d} — " +
                "run 'squeezr stats --reset' per workspace to clear.[/]");
        }
    }

    private static Table BuildAgentTable(UsageRollup rollup)
    {
        var table = new Table { Border = TableBorder.Rounded, Title = new TableTitle("By agent") };
        table.AddColumn("Client");
        table.AddColumn("Version");
        table.AddColumn("Count");
        table.AddColumn("Sessions");
        table.AddColumn("Last seen");
        foreach (var agent in rollup.Totals.Agents)
        {
            table.AddRow(
                agent.Client.EscapeMarkup(),
                (agent.ClientVersion ?? "-").EscapeMarkup(),
                agent.Count.ToString(CultureInfo.InvariantCulture),
                agent.Sessions.ToString(CultureInfo.InvariantCulture),
                $"{agent.LastSeen:yyyy-MM-dd HH:mm}");
        }

        return table;
    }

    // explicit zero sides beat silent ratios: a missing hook or a dead index each say exactly what is missing
    public static string AdoptionLine(SessionAdoption adoption) => adoption.HookSessions switch
    {
        0 when adoption.McpSessions == 0 => "Adoption: no session-attributed usage recorded yet.",
        0 => $"Adoption: {adoption.McpSessions} session(s) used MCP tools; read side unknown — 0 hook sessions recorded (install the pre-read hook).",
        _ => adoption.McpSessions == 0
            ? $"Adoption: 0 sessions used MCP tools; {adoption.HookSessions} session(s) read code via the hook."
            : $"Adoption: {adoption.McpSessions} of {adoption.HookSessions} reading session(s) used MCP tools (ratio {adoption.Ratio})."
    };

    private sealed class UsageLiveView
    {
        private readonly object _gate = new();
        private readonly Func<UsageRollup> _recompute;
        private readonly TimeSpan _interval;
        private LiveDisplayContext? _context;
        private UsageRollup _rollup;

        public UsageLiveView(Func<UsageRollup> recompute, TimeSpan interval)
        {
            _recompute = recompute;
            _interval = interval;
            _rollup = recompute();
        }

        public void Bind(LiveDisplayContext context) => _context = context;

        public void Recompute()
        {
            var rollup = _recompute();
            lock (_gate)
            {
                _rollup = rollup;
            }

            Redraw();
        }

        public IRenderable Render()
        {
            lock (_gate)
            {
                return BuildView(_rollup);
            }
        }

        private void Redraw()
        {
            if (_context is null)
            {
                return;
            }

            _context.UpdateTarget(Render());
            _context.Refresh();
        }

        private IRenderable BuildView(UsageRollup rollup)
        {
            var grid = new Grid();
            grid.AddColumn();
            grid.AddRow(
                $"[grey]live[/] — refresh every {(int)_interval.TotalSeconds}s, press Ctrl+C to stop");
            if (rollup.Totals.Records == 0)
            {
                grid.AddRow("No usage telemetry yet.");
                return grid;
            }

            foreach (var workspace in rollup.Workspaces)
            {
                var stale = workspace.Stale ? " [yellow](stale)[/]" : string.Empty;
                grid.AddRow(
                    $"[bold]{workspace.StoreKey.EscapeMarkup()}[/]{stale} — {workspace.Records} record(s)");
            }

            grid.AddRow(BuildAgentTable(rollup));
            grid.AddRow(
                $"Period: {rollup.Period.From:yyyy-MM-dd HH:mm} — {rollup.Period.To:yyyy-MM-dd HH:mm} " +
                $"({rollup.Totals.Records} records)");
            return grid;
        }
    }
}
