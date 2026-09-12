using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WatchCommand : Command<WatchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
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
            var workspaceRoot = Directory.GetCurrentDirectory();
            if (AnsiConsole.Profile.Capabilities.Interactive)
            {
                var status = new LiveWatchStatus(workspaceRoot);
                AnsiConsole.Live(status.Render()).Start(live =>
                {
                    status.Bind(live);
                    RunWatcher(status, workspaceRoot, cts.Token);
                });
            }
            else
            {
                RunWatcher(new PlainWatchStatus(), workspaceRoot, cts.Token,
                    () => Console.WriteLine($"watching {workspaceRoot} (press Ctrl+C to stop)"));
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onStop;
        }
    }

    private static void RunWatcher(
        IWatchStatus status,
        string workspaceRoot,
        CancellationToken cancellationToken,
        Action? onReady = null)
    {
        var watcher = new WorkspaceWatcher(status);
        watcher.StartAsync(workspaceRoot, cancellationToken).GetAwaiter().GetResult();
        onReady?.Invoke();
        cancellationToken.WaitHandle.WaitOne();
        watcher.StopAsync().GetAwaiter().GetResult();
    }

    private static string DescribeTotals(IndexResult result)
    {
        var added = result.Repos.Sum(repo => repo.Added);
        var modified = result.Repos.Sum(repo => repo.Modified);
        var deleted = result.Repos.Sum(repo => repo.Deleted);
        return $"+{added} ~{modified} -{deleted}";
    }

    private sealed class LiveWatchStatus(string workspaceRoot) : IWatchStatus
    {
        private readonly object _gate = new();
        private LiveDisplayContext? _context;
        private string _state = "waiting for changes";
        private string? _lastPass;
        private string? _error;

        public void Bind(LiveDisplayContext context) => _context = context;

        public void PassStarted(int changedPaths)
        {
            lock (_gate)
            {
                _state = $"indexing {changedPaths} changed paths";
            }
            Redraw();
        }

        public void PassCompleted(IndexResult result)
        {
            lock (_gate)
            {
                _state = "waiting for changes";
                _lastPass = $"{DateTime.Now:HH:mm:ss}  {DescribeTotals(result)}";
            }
            Redraw();
        }

        public void RetryScheduled(TimeSpan delay)
        {
            lock (_gate)
            {
                _state = $"index busy, retrying in {delay.TotalSeconds:F0}s";
            }
            Redraw();
        }

        public void Faulted(string error)
        {
            lock (_gate)
            {
                _error = error;
            }
            Redraw();
        }

        public IRenderable Render()
        {
            lock (_gate)
            {
                var grid = new Grid();
                grid.AddColumn();
                grid.AddColumn();
                grid.AddRow("[grey]workspace[/]", Markup.Escape(workspaceRoot));
                grid.AddRow("[grey]status[/]", Markup.Escape(_state));
                if (_lastPass is not null)
                {
                    grid.AddRow("[grey]last pass[/]", Markup.Escape(_lastPass));
                }
                if (_error is not null)
                {
                    grid.AddRow("[grey]error[/]", $"[red]{Markup.Escape(_error)}[/]");
                }
                return grid;
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
    }

    private sealed class PlainWatchStatus : IWatchStatus
    {
        private string? _lastFault;

        public void PassStarted(int changedPaths)
        {
            Console.WriteLine($"{Stamp()} pass started: {changedPaths} changed paths");
        }

        public void PassCompleted(IndexResult result)
        {
            _lastFault = null;
            Console.WriteLine($"{Stamp()} pass completed: {DescribeTotals(result)}");
            foreach (var warning in result.Warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }
        }

        public void RetryScheduled(TimeSpan delay)
        {
            Console.WriteLine($"{Stamp()} index busy, retrying in {delay.TotalSeconds:F0}s");
        }

        public void Faulted(string error)
        {
            if (error == _lastFault)
            {
                return;
            }

            _lastFault = error;
            Console.Error.WriteLine($"{Stamp()} error: {error}");
        }

        private static string Stamp() => DateTime.Now.ToString("HH:mm:ss");
    }
}
