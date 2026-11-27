using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Mcp.Tools;

[McpServerToolType]
public sealed class IndexStatusTool
{
    private readonly LiteWorkspace _workspace;
    private readonly LiteToolGate _gate;
    private readonly ClientIdentity _identity;

    public IndexStatusTool(LiteWorkspace workspace, LiteToolGate gate, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _identity = identity;
    }

    [Description("Workspace freshness and coverage, index-only: per-repo file/symbol/chunk counts, per-kind chunk counts (data.kindChunks maps \"code\" and \"docs\" to chunk totals; the values sum to the per-repo chunks counts), index age, and the resolved workspace rootPath — if rootPath is not your project's root, this session serves a different workspace. data.semantic reads \"unsupported\": this lite server carries no embeddings and no vector store, so the full product's embeddings/qdrant blocks are absent rather than zeroed. When more than one workspace is registered, data.workspaces also lists them as {name, rootPath, storeKey, current, stale} (up to 12 rows, the rest counted in workspacesOmitted) — an absent workspaces key means this session's workspace is the only one the registry knows.")]
    [McpServerTool(Name = "index_status", Title = "Index status")]
    public Task<object> GetAsync(CancellationToken cancellationToken = default) =>
        _gate.RunAsync(GetCore, cancellationToken);

    private Task<object> GetCore(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var warnings = new List<string>();

            var manager = new WorkspaceManager(root);
            var status = manager.GetStatus();
            GhostRefLag refsLag;
            IReadOnlyDictionary<string, int> kindChunks;
            using (var metaStore = new MetaStore(SqueezrPaths.MetaDbPath(root)))
            {
                refsLag = RefLookup.MeasureLag(metaStore);
                kindChunks = metaStore.CountChunksByKind();
            }

            if (refsLag.HasLag)
            {
                warnings.Add(
                    $"{refsLag.OverdueDeclarations} declaring file(s) carry {refsLag.LaggingRows} ref row(s) older than " +
                    $"a caller's newest index pass, {refsLag.ScannedRows} rows scanned — find_references marks those " +
                    $"answers stale and a booked pass re-parses them; '{LiteRemedy.IndexCommand}' reconciles all now.");
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
                repos.Add(new RepoData(r.Name, repoRoot, r.Files, r.Symbols, r.Chunks, ignored));
            }

            var siblings = WorkspaceSiblingSnapshot.Build(root);
            var reconcile = ReconcilePendingNotice.Read(root);
            warnings.AddRange(ReconcilePendingNotice.Lines(reconcile));
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
                SemanticUnsupported,
                kindChunks,
                BuildWorkspaceRows(siblings),
                siblings.Omitted,
                reconcile);

            warnings.Add(IndexerLine(SqueezrPaths.StoreDir(root)));

            var envelope = ToolEnvelope.Wrap(data, 0, false, warnings, root);
            QueryTelemetry.Record(root, _workspace.SessionId, "index_status",
                null, envelope.TokensUsed, envelope.Truncated, 1,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version, mapped: false);
            return Task.FromResult<object>(envelope);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Task.FromResult<object>(ToolErrors.From(ex, _workspace));
        }
    }

    private const string SemanticUnsupported = "unsupported";

    private static string IndexerLine(string storeDir)
    {
        var health = WatchStoreHealth.Probe(storeDir, DateTime.UtcNow);
        return health.IndexerRunning
            ? $"per-file freshness is not part of this aggregate report — a watcher pass keeps this index " +
              $"fresh (pid {health.OwnerPid}), and every query answer carries a content verdict that heals " +
              $"within the refresh budget"
            : "per-file freshness is not part of this aggregate report — no background indexer is running, " +
              $"so answers rely on the per-query content verdict (heals up to {RefreshBudget.MaxFilesReRead} " +
              $"file(s) per call); '{LiteRemedy.IndexCommand}' or a watcher pass reconciles the rest";
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
        var hasIgnored = repo.IgnoredTotal > 0 && repo.IgnoredBySource is { Count: > 0 };
        if (!hasIgnored && repo.TrackedButExcluded == 0)
            return null;

        var bySourceData = new Dictionary<string, int>();
        if (hasIgnored && repo.IgnoredBySource is { } ignoredBySource)
        {
            foreach (var (source, count) in ignoredBySource.OrderBy(kv => kv.Key))
            {
                if (count > 0)
                    bySourceData[source] = count;
            }

            var dominant = ignoredBySource.MaxBy(kv => kv.Value);
            if (dominant.Key is not null && dominant.Value > 0 && IgnoreSourceKey.IsKnown(dominant.Key))
            {
                warnings.Add(FormatIgnoreWarning(dominant.Key, dominant.Value));
            }
        }

        if (repo.TrackedButExcluded > 0)
        {
            warnings.Add(FormatTrackedButExcludedWarning(repo.TrackedButExcluded));
        }

        return new IgnoredData(hasIgnored ? repo.IgnoredTotal : 0, bySourceData, repo.TrackedButExcluded);
    }

    private static string FormatTrackedButExcludedWarning(int count) =>
        $"last pass kept {count} file(s) that .gitignore names but git tracks — tracked files are never " +
        "gitignore-dropped; 'git rm --cached <path>' or narrow the patterns if they should not be searched";

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
        [property: JsonPropertyName("semantic")] string Semantic,
        [property: JsonPropertyName("kindChunks")] IReadOnlyDictionary<string, int> KindChunks,
        [property: JsonPropertyName("workspaces"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<WorkspaceData>? Workspaces = null,
        [property: JsonPropertyName("workspacesOmitted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        int WorkspacesOmitted = 0,
        [property: JsonPropertyName("reconcile"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        WorktreeRecord? Reconcile = null);

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
        IgnoredData? Ignored);

    private sealed record IgnoredData(
        [property: JsonPropertyName("files")] int Files,
        [property: JsonPropertyName("bySource")] IReadOnlyDictionary<string, int> BySource,
        [property: JsonPropertyName("trackedButExcluded"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        int TrackedButExcluded = 0);
}
