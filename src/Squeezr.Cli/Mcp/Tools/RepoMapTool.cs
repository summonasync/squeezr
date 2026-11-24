using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class RepoMapTool
{
    private readonly McpWorkspace _workspace;
    private readonly McpToolGate _gate;
    private readonly ToolResponseCache? _cache;
    private readonly ClientIdentity _identity;

    // the render stays a plain RepoMapper in production; the seam is a whole render rather than a mapper
    // construction so a caller counts the maps one answer builds — a second build is what this shape
    // replaced, and it can be routed through a helper or a reused mapper without changing how many
    // mappers get constructed
    private readonly Func<string, string?, int, int, string?, RepoMapResult> _render;

    public RepoMapTool(
        McpWorkspace workspace,
        McpToolGate gate,
        ToolResponseCache? cache,
        ClientIdentity identity,
        Func<string, string?, int, int, string?, RepoMapResult>? renderer = null)
    {
        _workspace = workspace;
        _gate = gate;
        _cache = cache;
        _identity = identity;
        _render = renderer ??
                  ((root, repo, depth, maxTokens, prefix) => new RepoMapper(root).Build(repo, depth, maxTokens, prefix));
    }

    [Description("Structural map of the indexed repositories: nested directory/file tree rendered as indented lines with type/member signatures (depth 1 = dirs, 2 = files, 3 = members), never bodies. The cheapest first contact with an unfamiliar workspace — orient across repos, dirs and entry points before reading any file. Pass prefix (e.g. \"src/Squeezr.Cli/\") to render only that subtree with prefix-relative directory labels — depth keeps its structural meaning. Lines come from the index: one structural pass over the indexed rows reports files missing from disk or changed by size or mtime in staleFiles — a bounded sample of what it found, never the full verdict, with the covered row count and the sample size in freshness; the vanished rows come first in it, the first listedMissing of them, and the lagging rows after. Nothing is reindexed on this call; expand_span reads code on coordinates from other tools.")]
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
            var parameters = Params(repo, normalizedPrefix, depth, maxTokens, SecretGuardReader.Load(root).ExtraPatterns);
            if (_cache is not null && _cache.TryGet("repo_map", parameters, out var cached))
            {
                var hit = ((ToolEnvelope)cached!).MarkReplayed();
                QueryTelemetry.Record(root, _workspace.SessionId, "repo_map", parameters,
                    hit.PaidTokens, hit.Truncated, ((List<MapLineData>)hit.Data!).Count,
                    sw.ElapsedMilliseconds, cacheHit: true, indexAge: hit.IndexAge,
                    client: _identity.Name ?? "unknown", clientVersion: _identity.Version, mapped: false);
                return hit;
            }

            var warnings = new List<string>();
            var resolvedDepth = depth ?? RepoMapper.DefaultDepth;
            var resolvedMaxTokens = maxTokens ?? RepoMapper.DefaultMaxTokens;

            var result = _render(root, repo, resolvedDepth, resolvedMaxTokens, normalizedPrefix);

            // one structural pass over the rows this scope renders claims, against the disk as it is now;
            // the answer is not rebuilt behind it, so a dirty file costs a stat and not a second render
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

            // the answer carries per-file verdicts, so the line stops denying them and says which class of
            // check produced them instead — structure from a stat, never a content hash, never a refresh.
            // It speaks only when a verdict ships: freshness already names the class when nothing did. It also
            // ships how the sample is seated, because listedMissing decodes only against that order
            var sample = CarryStaleSample(freshness);
            string? classLine = sample.Carried > 0
                ? "repo_map answers structure, not content: staleFiles names indexed rows of this scope whose " +
                  "file is gone or whose size or mtime no longer matches the stamp, with no content read " +
                  "behind a verdict; the map lines themselves still come from the index, code_search and " +
                  $"symbol_lookup carry the content check for one file, and '{RemedyText.IndexCommand}' " +
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

            // the scope this answer speaks for is not the scope the pass reached: rows withheld or skipped
            // above are claims without a verdict, so a pass that covered nothing cannot read as clean
            var envelope = ToolEnvelope.Wrap(
                data, result.TokensUsed, result.Truncated, warnings, root, sample.Paths,
                freshness: AnswerFreshness.ForStructure(
                    checkedRows: freshness.Checked,
                    scopeRows: freshness.Checked + freshness.Withheld + freshness.Skipped,
                    staleTotal: freshness.Missing.Count + freshness.Changed.Count,
                    listed: sample.Carried,
                    listedMissing: sample.Missing.Count,
                    missing: freshness.Missing.Count,
                    changed: freshness.Changed.Count,
                    stalePaths: sample.Paths,
                    payloadLines: [classLine, sampleLine]));
            var invalidated = _cache?.TakeInvalidationWarning();
            QueryTelemetry.Record(root, _workspace.SessionId, "repo_map", parameters,
                envelope.PaidTokens, envelope.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version,
                cacheInvalidated: invalidated is not null, mapped: false);
            _cache?.Put("repo_map", parameters, envelope);
            return envelope.WithWarning(invalidated);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex, _workspace);
        }
    }

    private static object Params(
        string? repo,
        string? prefix,
        int? depth,
        int? maxTokens,
        IReadOnlyList<string> secretGuardExtra) =>
        new { repo, prefix, depth, maxTokens, secretGuardExtra };

    // The pass covers every row of the scope; an answer carries a bounded sample of its verdicts and names
    // what it left out. Both classes get seats, so a subtree that is mostly vanished files cannot hide the
    // rows that merely lag, and the leftover seats go to whichever class still has rows to show.
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
