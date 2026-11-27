using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Lite.Commands;
using Squeezr.Core.Storage;
using Squeezr.Lite.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Mcp.Tools;

[McpServerToolType]
public sealed class SymbolLookupTool
{
    private readonly LiteWorkspace _workspace;
    private readonly LiteToolGate _gate;
    private readonly ClientIdentity _identity;

    public SymbolLookupTool(LiteWorkspace workspace, LiteToolGate gate, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _identity = identity;
    }

    [Description("Look up an exact symbol by name across the indexed workspace: signature, doc comment, file:span, and body within the token budget (maxTokens, default 300) — the method you named, not the whole file. When a type's body does not fit the budget, the hit carries its member list (name, kind, line) plus membersOmitted instead, so the next call is expand_span on the member you want. Not-found returns an empty result, not an error; expand_span widens the context around a hit. Pass the symbol as 'name' or its alias 'symbol' — both are accepted, a wrong-param error names both.")]
    [McpServerTool(Name = "symbol_lookup", Title = "Look up symbol")]
    public Task<object> FindAsync(
        string? name = null,
        string? kind = null,
        string? repo = null,
        int? maxTokens = null,
        string? symbol = null,
        CancellationToken cancellationToken = default) =>
        _gate.RunAsync(ct => Task.FromResult(FindCore(name, symbol, kind, repo, maxTokens)), cancellationToken);

    private object FindCore(string? name, string? symbol, string? kind, string? repo, int? maxTokens)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var symbolName = SymbolParamAlias.Resolve("symbol_lookup", name, symbol, "name", "symbol");
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var budget = maxTokens ?? SymbolLookup.DefaultMaxTokens;
            var warnings = new List<string>();
            var result = new SymbolLookup(root).Find(symbolName, repo, kind, budget, body: true);

            var clipped = result.State is AnswerState.Clipped;

            if (result.GuardWarnings is { } withheld)
            {
                warnings.AddRange(withheld);
            }

            if (clipped)
            {
                warnings.Add(HintText.TruncationLine(
                    result.TokensUsed,
                    budget,
                    RemedyFor(result)));
            }

            if (result.Matches.Count == 0 && result.DidYouMean is null && result.GuardWarnings is null)
            {
                warnings.Add("No symbol found.");
            }

            var data = result.Matches.Select(m => new SymbolHitData(
                m.Repo, m.File, $"{m.StartLine}-{m.EndLine}", m.Signature, m.DocComment, m.Body,
                m.Members.Count == 0
                    ? null
                    : m.Members.Select(member => new SymbolMemberData(member.Name, member.Kind, member.Line)).ToList(),
                m.MembersOmitted,
                m.BodyOmitted)).ToList();

            var namedFiles = data.Select(d => (d.Repo, d.File)).ToList();
            var (cost, unknownBaselineFiles) = CostBaseline.Measure(
                root, namedFiles, result.TokensUsed, !clipped);
            if (WriterNotice.For(root) is { } writerNotice)
            {
                warnings.Add(writerNotice);
            }
            if (unknownBaselineFiles > 0)
            {
                warnings.Add(
                    $"{unknownBaselineFiles} hit file(s) not in the index — excluded from the whole-file baseline");
            }

            var envelope = ToolEnvelope.Wrap(data, result.TokensUsed, clipped, warnings, root,
                result.DidYouMean, cost: cost);
            QueryTelemetry.Record(root, _workspace.SessionId, "symbol_lookup",
                new { name = symbolName, kind, repo, maxTokens = budget },
                envelope.TokensUsed, envelope.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version,
                mapped: !envelope.Truncated && data.Any(static hit => hit.BodyOmitted));
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or McpUsageException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex, _workspace);
        }
    }

    private static string RemedyFor(SymbolLookupResult result) =>
        result.RefusedBodyTokens > 0 && result.Matches.All(static m => m.Members.Count == 0)
            ? HintText.BodyPriceRemedy(result.RefusedBodyTokens)
            : result.Hint?.ForMcp() ?? HintText.RaiseMaxTokensRemedy;

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
        int MembersOmitted,
        [property: JsonPropertyName("bodyOmitted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool BodyOmitted);

    private sealed record SymbolMemberData(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("line")] int Line);
}
