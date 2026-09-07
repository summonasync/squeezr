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

    public IndexStatusTool(McpWorkspace workspace, McpToolGate gate)
    {
        _workspace = workspace;
        _gate = gate;
    }

    /// <summary>
    /// Workspace freshness: per-repo file/symbol/chunk counts, pending embeddings, Qdrant availability
    /// and point count, index age. Call before code_search to decide whether a reindex is needed.
    /// </summary>
    [McpServerTool(Name = "index_status", Title = "Index status")]
    public Task<object> GetAsync(CancellationToken cancellationToken = default) =>
        _gate.RunAsync(GetCore, cancellationToken);

    private async Task<object> GetCore(CancellationToken cancellationToken)
    {
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
                status.Repos.Select(r => new RepoData(r.Name, r.Files, r.Symbols, r.Chunks)).ToList(),
                new EmbeddingsData(qdrant.PendingChunks),
                new QdrantData(qdrant.Available, qdrant.Points));

            return ToolEnvelope.Wrap(data, 0, false, warnings, root, staleFiles: null);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private sealed record IndexStatusData(
        [property: JsonPropertyName("workspace")] string Workspace,
        [property: JsonPropertyName("repos")] IReadOnlyList<RepoData> Repos,
        [property: JsonPropertyName("embeddings")] EmbeddingsData Embeddings,
        [property: JsonPropertyName("qdrant")] QdrantData Qdrant);

    private sealed record RepoData(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("files")] int Files,
        [property: JsonPropertyName("symbols")] int Symbols,
        [property: JsonPropertyName("chunks")] int Chunks);

    private sealed record EmbeddingsData(
        [property: JsonPropertyName("pending")] int Pending);

    private sealed record QdrantData(
        [property: JsonPropertyName("available")] bool Available,
        [property: JsonPropertyName("points")] long? Points);
}
