using Squeezr.Core.Indexing;

namespace Squeezr.Cli.Output;

public sealed class IndexEventsRenderer : IIndexProgress
{
    private const int EmbeddingsProgressInterval = 512;

    private readonly IndexEventWriter _writer;
    private readonly Dictionary<string, IndexPhase> _phases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _phaseSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _embeddedChunks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _embeddingTotals = new(StringComparer.OrdinalIgnoreCase);
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
        _embeddedChunks.Clear();
        _embeddingTotals.Clear();
    }

    public void PhaseStarted(string repoName, IndexPhase phase, int totalItems)
    {
        _phases[repoName] = phase;
        _phaseSymbols[repoName] = 0;
        _embeddedChunks[repoName] = 0;
        _embeddingTotals[repoName] = totalItems;
        if (phase is not (IndexPhase.Walk or IndexPhase.Files) && totalItems > 0)
        {
            _writer.Progress(repoName, phase, totalItems);
        }
    }

    public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null)
    {
        var phase = _phases.GetValueOrDefault(repoName);

        if (phase is IndexPhase.Files)
        {
            _files++;
            _writer.FileIndexed(repoName, currentItem, _files);
        }

        if (phase is IndexPhase.Vectors)
        {
            var done = ++_embeddedChunks[repoName];
            if (done % EmbeddingsProgressInterval == 0)
            {
                _writer.Progress(repoName, phase, _embeddingTotals.GetValueOrDefault(repoName), done);
            }
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
