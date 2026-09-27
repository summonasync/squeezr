using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class IndexStatusTool
{
    private readonly McpWorkspace _workspace;
    private IVectorStore? _vectorStore;
    private bool _resolved;
    private readonly McpToolGate _gate;
    private readonly ClientIdentity _identity;

    public IndexStatusTool(McpWorkspace workspace, McpToolGate gate, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _identity = identity;
    }

    [Description("Workspace freshness and coverage: per-repo file/symbol/chunk counts, pending embeddings, semantic coverage (embedded share of chunks, 0..1; null when there is nothing to embed), Qdrant availability and point count, index age, and the resolved workspace rootPath — if rootPath is not your project's root, this session serves a different workspace. Check it before heavy querying to see which repos and files the index covers and whether a reindex is needed.")]
    [McpServerTool(Name = "index_status", Title = "Index status")]
    public Task<object> GetAsync(CancellationToken cancellationToken = default) =>
        _gate.RunAsync(GetCore, cancellationToken);

    private async Task<object> GetCore(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var warnings = new List<string>();
            if (!_resolved)
            {
                warnings.AddRange(EmbeddingRuntimeFactory.TryCreate(root, withProvider: false, out _, out var store));
                _vectorStore = store;
                _resolved = true;
            }

            var status = new WorkspaceManager(root).GetStatus();
            QdrantStatus qdrant;
            using (var metaStore = new MetaStore(SqueezrPaths.MetaDbPath(root)))
            {
                qdrant = await QdrantStatusProbe.ProbeAsync(_vectorStore, metaStore, cancellationToken);
            }

            var data = new IndexStatusData(
                status.Name,
                root,
                status.Repos.Select(r => new RepoData(
                    r.Name,
                    Path.GetFullPath(Path.IsPathRooted(r.Path) ? r.Path : Path.Combine(root, r.Path)),
                    r.Files,
                    r.Symbols,
                    r.Chunks)).ToList(),
                new EmbeddingsData(
                    qdrant.PendingChunks,
                    new SemanticCoverage(qdrant.TotalChunks, qdrant.PendingChunks).Ratio),
                new QdrantData(qdrant.Available, qdrant.Points));

            var envelope = ToolEnvelope.Wrap(data, 0, false, warnings, root, staleFiles: null);
            QueryTelemetry.Record(root, _workspace.SessionId, "index_status",
                null, envelope.TokensUsed, envelope.Truncated, 1,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version);
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private sealed record IndexStatusData(
        [property: JsonPropertyName("workspace")] string Workspace,
        [property: JsonPropertyName("rootPath")] string RootPath,
        [property: JsonPropertyName("repos")] IReadOnlyList<RepoData> Repos,
        [property: JsonPropertyName("embeddings")] EmbeddingsData Embeddings,
        [property: JsonPropertyName("qdrant")] QdrantData Qdrant);

    private sealed record RepoData(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rootPath")] string RootPath,
        [property: JsonPropertyName("files")] int Files,
        [property: JsonPropertyName("symbols")] int Symbols,
        [property: JsonPropertyName("chunks")] int Chunks);

    private sealed record EmbeddingsData(
        [property: JsonPropertyName("pending")] int Pending,
        [property: JsonPropertyName("coverage")] double? Coverage);

    private sealed record QdrantData(
        [property: JsonPropertyName("available")] bool Available,
        [property: JsonPropertyName("points")] long? Points);
}
