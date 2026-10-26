using System.Text.Json;
using Squeezr.Core.Indexing;
using Squeezr.Core.Serialization;

namespace Squeezr.Cli.Output;

public sealed class IndexEventWriter
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();

    public IndexEventWriter(TextWriter writer) => _writer = writer;

    public void Started(string workspace, int? changedPaths = null, string? provider = null) =>
        Write(new IndexStartedLine("started", workspace, changedPaths, provider));

    public void FileIndexed(string repo, string path, int files) =>
        Write(new IndexFileLine("fileIndexed", repo, path, files));

    public void Progress(string repo, IndexPhase phase, int total, int? done = null) =>
        Write(new IndexProgressLine("progress", repo, PhaseName(phase), total, done));

    public void Completed(IndexResult result, int durationMs, int? symbols, string? provider = null) =>
        Write(IndexCompletedLine.FromResult(result, durationMs, symbols, provider));

    public void Warning(string code, string message) =>
        Write(new IndexWarningLine("warning", code, message));

    public void Error(string code, string message) =>
        Write(new IndexErrorLine("error", code, message));

    private void Write<T>(T line)
    {
        lock (_gate)
        {
            _writer.WriteLine(JsonSerializer.Serialize(line, SqueezrJson.OutputOptions));
            _writer.Flush();
        }
    }

    private static string PhaseName(IndexPhase phase) => phase switch
    {
        IndexPhase.Walk => "walk",
        IndexPhase.Files => "files",
        IndexPhase.Semantic => "semantic",
        IndexPhase.Embeddings => "embeddings",
        IndexPhase.Trigram => "trigram",
        _ => phase.ToString().ToLowerInvariant()
    };
}
