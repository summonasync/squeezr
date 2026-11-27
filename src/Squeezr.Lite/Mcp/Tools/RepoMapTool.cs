using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;
using Squeezr.Core.Storage;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Mcp.Tools;

[McpServerToolType]
public sealed class RepoMapTool
{
    private readonly LiteWorkspace _workspace;
    private readonly LiteToolGate _gate;
    private readonly ClientIdentity _identity;
    private readonly Func<string, string?, int, int, string?, RepoMapResult> _render;

    public RepoMapTool(
        LiteWorkspace workspace,
        LiteToolGate gate,
        ClientIdentity identity,
        Func<string, string?, int, int, string?, RepoMapResult>? renderer = null)
    {
        _workspace = workspace;
        _gate = gate;
        _identity = identity;
        _render = renderer ??
                  ((root, repo, depth, maxTokens, prefix) => new RepoMapper(root).Build(repo, depth, maxTokens, prefix));
    }

    [Description("Structural map of the indexed repositories: nested directory/file tree rendered as indented lines with type/member signatures (depth 1 = dirs, 2 = files, 3 = members), never bodies. The cheapest first contact with an unfamiliar workspace — orient across repos, dirs and entry points before reading any file. Pass prefix (e.g. \"src/Squeezr.Cli/\") to render only that subtree with prefix-relative directory labels — depth keeps its structural meaning. Lines come from the index: one structural pass over the indexed rows reports files missing from disk or changed by size or mtime in staleFiles — a bounded sample of what it found, never the full verdict, with the covered row count and the sample size stated in warnings. Nothing is reindexed on this call; expand_span reads code on coordinates from other tools.")]
    [McpServerTool(Name = "repo_map", Title = "Repository map")]
    public Task<object> BuildAsync(
        string? repo = null,
        string? prefix = null,
        int? depth = null,
        int? maxTokens = null,
        CancellationToken cancellationToken = default) =>
        _gate.RunAsync(ct => Task.FromResult(BuildCore(repo, prefix, depth, maxTokens)), cancellationToken);

    private object BuildCore(string? repo, string? prefix, int? depth, int? maxTokens)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var normalizedPrefix = PathPrefix.Normalize(prefix);
            var warnings = new List<string>();
            var resolvedDepth = depth ?? RepoMapper.DefaultDepth;
            var resolvedMaxTokens = maxTokens ?? RepoMapper.DefaultMaxTokens;

            var result = _render(root, repo, resolvedDepth, resolvedMaxTokens, normalizedPrefix);
            var freshness = StructureFreshnessCheck.Check(root, result.Repos, result.ScopedPrefix);

            if (result.GuardWarnings is { } withheld)
            {
                warnings.AddRange(withheld);
            }

            if (result.Truncated)
            {
                warnings.Add(HintText.TruncationLine(
                    result.TokensUsed,
                    resolvedMaxTokens,
                    result.Hint?.ForMcp() ?? HintText.RaiseMaxTokensRemedy));
            }

            var data = result.Lines.Select(l => new MapLineData(l.Depth, l.Text)).ToList();

            var sample = CarryStaleSample(freshness);
            string? classLine = sample.Carried > 0
                ? "repo_map answers structure, not content: staleFiles names indexed rows of this scope whose " +
                  "file is gone or whose size or mtime no longer matches the stamp, with no content read " +
                  "behind a verdict; the map lines themselves still come from the index, and 'sqzrlite index' " +
                  "reconciles the rows; the vanished rows come first"
                : null;
            if (classLine is not null)
            {
                warnings.Add(classLine);
            }

            string? sampleLine = sample.Omitted > 0
                ? $"structure pass covered {freshness.Checked} indexed rows and found " +
                  $"{freshness.Missing.Count} missing + {freshness.Changed.Count} changed; staleFiles carries " +
                  $"a sample of {sample.Carried} of them, {sample.Omitted} stale rows are not listed"
                : null;
            if (sampleLine is not null)
            {
                warnings.Add(sampleLine);
            }

            var envelope = ToolEnvelope.Wrap(
                data, result.TokensUsed, result.Truncated, warnings, root, sample.Paths);
            QueryTelemetry.Record(root, _workspace.SessionId, "repo_map",
                new { repo, prefix = normalizedPrefix, depth, maxTokens },
                envelope.TokensUsed, envelope.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version, mapped: false);
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex, _workspace);
        }
    }

    private static StaleSample CarryStaleSample(StructureFreshnessResult freshness)
    {
        var limit = StructureFreshnessCheck.StaleSampleLimit;
        var missingSeats = Math.Min(limit / 2, freshness.Missing.Count);
        var changedSeats = Math.Min(limit - missingSeats, freshness.Changed.Count);
        missingSeats = Math.Min(freshness.Missing.Count, missingSeats + limit - missingSeats - changedSeats);

        return new StaleSample(
            [.. freshness.Missing.Take(missingSeats)],
            [.. freshness.Changed.Take(changedSeats)],
            freshness.Missing.Count + freshness.Changed.Count - missingSeats - changedSeats);
    }

    private sealed record StaleSample(
        IReadOnlyList<string> Missing,
        IReadOnlyList<string> Changed,
        int Omitted)
    {
        public int Carried => Missing.Count + Changed.Count;

        public IReadOnlyList<string> Paths => [.. Missing, .. Changed];
    }

    private sealed record MapLineData(
        [property: JsonPropertyName("depth")] int Depth,
        [property: JsonPropertyName("text")] string Text);
}
