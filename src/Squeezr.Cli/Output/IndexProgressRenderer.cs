using Spectre.Console;
using Squeezr.Core.Indexing;

namespace Squeezr.Cli.Output;

public sealed class IndexProgressRenderer : IIndexProgress
{
    private readonly ProgressContext _context;
    private readonly Dictionary<string, ProgressTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IndexPhase> _phases = new(StringComparer.OrdinalIgnoreCase);

    public IndexProgressRenderer(ProgressContext context) => _context = context;

    public void PhaseStarted(string repoName, IndexPhase phase, int totalItems)
    {
        _phases[repoName] = phase;
        if (!_tasks.TryGetValue(repoName, out var task))
        {
            task = _context.AddTask(Describe(repoName, phase, null), maxValue: Math.Max(totalItems, 1));
            _tasks[repoName] = task;
        }
        else
        {
            task.Value = 0;
            task.MaxValue = Math.Max(totalItems, 1);
            task.Description = Describe(repoName, phase, null);
        }

        task.IsIndeterminate = totalItems == 0;
    }

    public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null)
    {
        if (!_tasks.TryGetValue(repoName, out var task))
        {
            return;
        }

        task.IsIndeterminate = false;
        task.Increment(1);
        var phase = _phases.GetValueOrDefault(repoName);
        task.Description = Describe(repoName, phase, symbolsInserted);
    }

    public void RepoCompleted(string repoName)
    {
        if (!_tasks.TryGetValue(repoName, out var task))
        {
            return;
        }

        task.Value = task.MaxValue;
        task.Description = $"[green]{Markup.Escape(repoName)}[/]";
        task.StopTask();
    }

    private static string Describe(string repoName, IndexPhase phase, int? symbolsInserted)
    {
        var label = phase switch
        {
            IndexPhase.Walk => "walking",
            IndexPhase.Files => "files",
            IndexPhase.Semantic => "semantic",
            IndexPhase.Embeddings => "embeddings",
            IndexPhase.Trigram => "trigram",
            _ => phase.ToString()
        };

        var counters = symbolsInserted is > 0 ? $" [grey](symbols: {symbolsInserted})[/]" : string.Empty;
        return $"[bold]{Markup.Escape(repoName)}[/] [grey]{label}[/]{counters}";
    }
}
