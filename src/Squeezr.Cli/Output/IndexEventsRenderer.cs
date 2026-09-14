using Squeezr.Core.Indexing;

namespace Squeezr.Cli.Output;

public sealed class IndexEventsRenderer : IIndexProgress
{
    private readonly IndexEventWriter _writer;
    private readonly Dictionary<string, IndexPhase> _phases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _phaseSymbols = new(StringComparer.OrdinalIgnoreCase);
    private int _files;
    private int _symbols;

    public IndexEventsRenderer(IndexEventWriter writer) => _writer = writer;

    public int Symbols => _symbols;

    public void Reset()
    {
        _files = 0;
        _symbols = 0;
        _phases.Clear();
        _phaseSymbols.Clear();
    }

    public void PhaseStarted(string repoName, IndexPhase phase, int totalItems)
    {
        _phases[repoName] = phase;
        _phaseSymbols[repoName] = 0;
        if (phase is not (IndexPhase.Walk or IndexPhase.Files) && totalItems > 0)
        {
            _writer.Progress(repoName, phase, totalItems);
        }
    }

    public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null)
    {
        if (_phases.GetValueOrDefault(repoName) is IndexPhase.Files)
        {
            _files++;
            _writer.FileIndexed(repoName, currentItem, _files);
        }

        if (symbolsInserted is { } symbols)
        {
            _phaseSymbols.TryGetValue(repoName, out var last);
            _symbols += symbols - last;
            _phaseSymbols[repoName] = symbols;
        }
    }

    public void RepoCompleted(string repoName)
    {
    }
}
