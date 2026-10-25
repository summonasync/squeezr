using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
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

    [Description("Workspace freshness and coverage: per-repo file/symbol/chunk counts, pending embeddings, semantic coverage (embedded share of chunks, 0..1; null when there is nothing to embed), per-kind chunk counts (data.kindChunks maps \"code\" and \"docs\" to chunk totals; the values sum to the per-repo chunks counts), Qdrant availability and point count, index age, and the resolved workspace rootPath — if rootPath is not your project's root, this session serves a different workspace. Check it before heavy querying to see which repos and files the index covers and whether a reindex is needed. When more than one workspace is registered, data.workspaces also lists them as {name, rootPath, storeKey, current, stale} (up to 12 rows, the rest counted in workspacesOmitted) — an absent workspaces key means this session's workspace is the only one the registry knows.")]
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

            var manager = new WorkspaceManager(root);
            var status = manager.GetStatus();
            QdrantStatus qdrant;
            GhostRefLag refsLag;
            IReadOnlyDictionary<string, int> kindChunks;
            using (var metaStore = new MetaStore(SqueezrPaths.MetaDbPath(root)))
            {
                qdrant = await QdrantStatusProbe.ProbeAsync(_vectorStore, metaStore, cancellationToken);
                refsLag = RefLookup.MeasureLag(metaStore);
                kindChunks = metaStore.CountChunksByKind();
            }

            if (qdrant.ExcessPoints is { } excess)
            {
                warnings.Add(
                    $"the collection carries {excess} more point(s) than the index has chunks — orphan vectors " +
                    "no live chunk hash maps to; 'squeezr vectors reconcile' lists them, '--apply' drops them.");
            }

            if (refsLag.HasLag)
            {
                warnings.Add(
                    $"{refsLag.OverdueDeclarations} declaring file(s) carry {refsLag.LaggingRows} ref row(s) older than " +
                    $"a caller's newest index pass, {refsLag.ScannedRows} rows scanned — find_references marks those " +
                    $"answers stale and a booked pass re-parses them; '{RemedyText.IndexCommand}' reconciles all now.");
            }

            var rankExclusion = RankExclusion.Load(root);
            warnings.AddRange(rankExclusion.Warnings);
            if (rankExclusion.IsActive)
            {
                var listed = string.Join(", ", rankExclusion.Patterns.Select(pattern => $"'{pattern}'"));
                warnings.Add(
                    $"indexing.rankExclude is active: code_search ranking drops {listed} — those files stay " +
                    "indexed, so expand_span / find_references / symbol_lookup still reach them.");
            }

            var repos = new List<RepoData>();
            foreach (var r in status.Repos)
            {
                var repoRoot = Path.GetFullPath(Path.IsPathRooted(r.Path) ? r.Path : Path.Combine(root, r.Path));
                var ignored = BuildIgnoredData(r, warnings);
                repos.Add(new RepoData(r.Name, repoRoot, r.Files, r.Symbols, r.Chunks, ignored, RepoGuardReport.FromRepo(r)));
            }

            var withheld = RepoGuardReport.FromTally(status.Repos);
            if (withheld is not null)
            {
                warnings.Add(withheld.Warning());
            }

            var siblings = WorkspaceSiblingSnapshot.Build(root);
            if (status.Repos.Count == 1)
            {
                var scopeHint = RemedyText.ScopeBlindSiblings(siblings.LiveSiblings);
                if (scopeHint is not null)
                {
                    warnings.Add(scopeHint);
                }
            }

            var data = new IndexStatusData(
                status.Name,
                root,
                repos,
                new EmbeddingsData(
                    qdrant.PendingChunks,
                    new SemanticCoverage(qdrant.TotalChunks, qdrant.PendingChunks).Ratio),
                new QdrantData(qdrant.Available, qdrant.Points),
                kindChunks,
                BuildWorkspaceRows(siblings),
                siblings.Omitted);

            var envelope = ToolEnvelope.Wrap(data, 0, false, warnings, root, staleFiles: null);
            QueryTelemetry.Record(root, _workspace.SessionId, "index_status",
                null, envelope.TokensUsed, envelope.Truncated, 1,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version);
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex, _workspace);
        }
    }

    private static IReadOnlyList<WorkspaceData>? BuildWorkspaceRows(WorkspaceSiblingSnapshot snapshot)
    {
        if (snapshot.Workspaces.Count == 0)
            return null;

        var rows = new List<WorkspaceData>(snapshot.Workspaces.Count);
        foreach (var entry in snapshot.Workspaces)
        {
            rows.Add(new WorkspaceData(entry.Name, entry.RootPath, entry.StoreKey, entry.Current, entry.Stale));
        }

        return rows;
    }

    private static IgnoredData? BuildIgnoredData(RepoStatus repo, List<string> warnings)
    {
        if (repo.IgnoredTotal == 0 || repo.IgnoredBySource is null || repo.IgnoredBySource.Count == 0)
            return null;

        var dominant = repo.IgnoredBySource.MaxBy(kv => kv.Value);
        if (dominant.Key is not null && dominant.Value > 0 && IgnoreSourceKey.IsKnown(dominant.Key))
        {
            warnings.Add(FormatIgnoreWarning(dominant.Key, dominant.Value));
        }

        var bySourceData = new Dictionary<string, int>();
        foreach (var (source, count) in repo.IgnoredBySource.OrderBy(kv => kv.Key))
        {
            if (count > 0)
                bySourceData[source] = count;
        }

        return new IgnoredData(repo.IgnoredTotal, bySourceData);
    }

    private static string FormatIgnoreWarning(string sourceKey, int count)
    {
        var via = sourceKey switch
        {
            "defaults" => "built-in defaults",
            "config" => "indexing.ignore config",
            "squeezrignore" => ".squeezrignore",
            "gitignore" => ".gitignore",
            _ => sourceKey
        };
        var hint = sourceKey switch
        {
            "gitignore" => " — set indexing.respectGitignore=false to disable",
            "squeezrignore" => " — remove .squeezrignore or adjust patterns to include them",
            "config" => " — adjust indexing.ignore in the workspace config",
            _ => " — adjust indexing.ignore defaults via workspace config"
        };
        return $"last index pass excluded {count} file(s) via {via}{hint}";
    }

    private sealed record IndexStatusData(
        [property: JsonPropertyName("workspace")] string Workspace,
        [property: JsonPropertyName("rootPath")] string RootPath,
        [property: JsonPropertyName("repos")] IReadOnlyList<RepoData> Repos,
        [property: JsonPropertyName("embeddings")] EmbeddingsData Embeddings,
        [property: JsonPropertyName("qdrant")] QdrantData Qdrant,
        [property: JsonPropertyName("kindChunks")] IReadOnlyDictionary<string, int> KindChunks,
        [property: JsonPropertyName("workspaces"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<WorkspaceData>? Workspaces = null,
        [property: JsonPropertyName("workspacesOmitted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        int WorkspacesOmitted = 0);

    private sealed record WorkspaceData(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rootPath")] string RootPath,
        [property: JsonPropertyName("storeKey")] string StoreKey,
        [property: JsonPropertyName("current")] bool Current,
        [property: JsonPropertyName("stale")] bool Stale);

    private sealed record RepoData(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rootPath")] string RootPath,
        [property: JsonPropertyName("files")] int Files,
        [property: JsonPropertyName("symbols")] int Symbols,
        [property: JsonPropertyName("chunks")] int Chunks,
        [property: JsonPropertyName("ignored"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IgnoredData? Ignored,
        [property: JsonPropertyName("guard"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RepoGuardReport? Guard);

    private sealed record IgnoredData(
        [property: JsonPropertyName("files")] int Files,
        [property: JsonPropertyName("bySource")] IReadOnlyDictionary<string, int> BySource);

    private sealed record EmbeddingsData(
        [property: JsonPropertyName("pending")] int Pending,
        [property: JsonPropertyName("coverage")] double? Coverage);

    private sealed record QdrantData(
        [property: JsonPropertyName("available")] bool Available,
        [property: JsonPropertyName("points")] long? Points);
}
