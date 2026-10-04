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
public sealed class SymbolLookupTool
{
    private const int GuardCandidateLimit = 50;

    private readonly McpWorkspace _workspace;
    private readonly McpToolGate _gate;
    private readonly ToolResponseCache? _cache;
    private readonly ClientIdentity _identity;

    public SymbolLookupTool(McpWorkspace workspace, McpToolGate gate, ToolResponseCache? cache, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _cache = cache;
        _identity = identity;
    }

    [Description("Look up an exact symbol by name across the indexed workspace: signature, doc comment, file:span, and body within the token budget (maxTokens, default 300) — the method you named, not the whole file. When a type's body does not fit the budget, the hit carries its member list (name, kind, line) plus membersOmitted instead, so the next call is expand_span on the member you want. Not-found returns an empty result, not an error; expand_span widens the context around a hit.")]
    [McpServerTool(Name = "symbol_lookup", Title = "Look up symbol")]
    public object Find(
        string name,
        string? kind = null,
        string? repo = null,
        int? maxTokens = null) =>
        _gate.Run(() => FindCore(name, kind, repo, maxTokens));

    private object FindCore(
        string name,
        string? kind,
        string? repo,
        int? maxTokens)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var parameters = Params(name, kind, repo, SecretGuardReader.Load(root).ExtraPatterns);
            if (_cache is not null && _cache.TryGet("symbol_lookup", parameters, out var cached))
            {
                var hit = (ToolEnvelope)cached!;
                QueryTelemetry.Record(root, _workspace.SessionId, "symbol_lookup", parameters,
                    hit.TokensUsed, hit.Truncated, ((List<SymbolHitData>)hit.Data!).Count,
                    sw.ElapsedMilliseconds, cacheHit: true, indexAge: hit.IndexAge,
                    client: _identity.Name ?? "unknown", clientVersion: _identity.Version);
                return hit;
            }

            var warnings = new List<string>();
            var budget = maxTokens ?? SymbolLookup.DefaultMaxTokens;
            var lookup = new SymbolLookup(root);

            var result = lookup.Find(name, repo, kind, budget, body: true);

            var freshness = Guard(root, result);
            if (freshness.Refreshed.Count > 0)
            {
                result = lookup.Find(name, repo, kind, budget, body: true);
            }

            if (result.GuardWarnings is { } withheld)
            {
                warnings.AddRange(withheld);
            }

            if (result.Hint is not null)
            {
                warnings.Add(result.Hint.ForMcp());
            }

            if (result.Matches.Count == 0 && result.DidYouMean is null && result.GuardWarnings is null)
            {
                warnings.Add("No symbol found — use code_search for paraphrase queries.");
            }

            var data = result.Matches.Select(m => new SymbolHitData(
                m.Repo, m.File, $"{m.StartLine}-{m.EndLine}", m.Signature, m.DocComment, m.Body,
                m.Members.Count == 0
                    ? null
                    : m.Members.Select(member => new SymbolMemberData(member.Name, member.Kind, member.Line)).ToList(),
                m.MembersOmitted)).ToList();

            var envelope = ToolEnvelope.Wrap(data, result.TokensUsed, result.Truncated, warnings, root, freshness.Stale,
                result.DidYouMean);
            var invalidated = _cache?.TakeInvalidationWarning();
            QueryTelemetry.Record(root, _workspace.SessionId, "symbol_lookup", parameters,
                result.TokensUsed, result.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version,
                cacheInvalidated: invalidated is not null);
            _cache?.Put("symbol_lookup", parameters, envelope);
            return envelope.WithWarning(invalidated);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private static FreshnessResult Guard(string root, SymbolLookupResult result) =>
        FreshnessGuard.CheckAndRefresh(
            root,
            result.Matches.Take(GuardCandidateLimit).Select(m => (m.Repo, m.File)).Distinct().ToList(),
            StaleLockWarningSink.ToStderr);

    // the guard re-reads its widening patterns from the config on every call, so a cached answer is only
    // valid for the pattern set it was computed with
    private static object Params(
        string name,
        string? kind,
        string? repo,
        IReadOnlyList<string> secretGuardExtra) =>
        new { name, kind, repo, secretGuardExtra };

    private sealed record SymbolHitData(
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("span")] string Span,
        [property: JsonPropertyName("signature")] string? Signature,
        [property: JsonPropertyName("docComment")] string? DocComment,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("members"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<SymbolMemberData>? Members,
        [property: JsonPropertyName("membersOmitted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        int MembersOmitted);

    private sealed record SymbolMemberData(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("line")] int Line);
}
