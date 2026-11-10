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
public sealed class CodePackTool
{
    private const int DefaultMaxTokens = 2000;

    private readonly McpWorkspace _workspace;
    private readonly McpToolGate _gate;
    private readonly ToolResponseCache? _cache;
    private readonly ClientIdentity _identity;

    public CodePackTool(McpWorkspace workspace, McpToolGate gate, ToolResponseCache? cache, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _cache = cache;
        _identity = identity;
    }

    [Description("Answer \"what is this symbol and who touches it\" in ONE call: definition (signature, doc comment, file:span), the defining member's body within the budget (maxTokens default 2000), every caller as file:line rows, and per-file cost — instead of chaining symbol_lookup → expand_span → find_references. Budget order: definition and body spend first, callers truncate (the withheld count is in the response). complete:false names the way out; expand_span reads the exact lines around any coordinate. Per-tool exactness stays available: symbol_lookup, find_references and expand_span keep their contracts.")]
    [McpServerTool(Name = "code_pack", Title = "Code pack")]
    public object Pack(
        string name,
        string? repo = null,
        string? kind = null,
        int? maxTokens = null) =>
        _gate.Run(() => PackCore(name, repo, kind, maxTokens));

    private object PackCore(string name, string? repo, string? kind, int? maxTokens)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var budget = maxTokens ?? DefaultMaxTokens;
            var parameters = new
            {
                name,
                repo,
                kind,
                maxTokens = budget,
                secretGuardExtra = SecretGuardReader.Load(root).ExtraPatterns,
            };
            if (_cache is not null && _cache.TryGet("code_pack", parameters, out var cached))
            {
                var hit = (ToolEnvelope)cached!;
                RecordLedger(root, parameters, hit, sw, cacheHit: true, cacheInvalidated: false);
                return hit;
            }

            var warnings = new List<string>();
            var pack = new SymbolPackComposer(root).Compose(name, repo, kind, budget);

            if (pack.File.Length == 0)
            {
                warnings.Add($"No symbol named '{name}' found — use code_search for paraphrase queries.");
            }

            if (pack.WithheldCallers > 0)
            {
                warnings.Add(
                    $"{pack.WithheldCallers} caller(s) withheld to fit the budget — raise maxTokens, or read the callers list");
            }

            if (pack.LookupTruncated)
            {
                warnings.Add(HintText.TruncationLine(pack.LookupTokens, budget, RemedyFor(pack)));
            }

            var tokens = pack.LookupTokens + pack.CallerTokens;
            AnswerCost cost;
            IReadOnlyList<FileFreshness>? fileFreshness = null;
            var unknownBaselineFiles = 0;
            if (pack.File.Length > 0)
            {
                var files = new List<(string Repo, string Path)> { (pack.Repo, pack.File) };
                files.AddRange(pack.Callers.Select(c => (c.Repo, c.File)));
                (cost, unknownBaselineFiles) = CostBaseline.Measure(root, files, tokens, pack.Complete);
                fileFreshness = FreshnessVerdict.Classify(root, files);
                if (StoreWriterNotice.For(root) is { } writerNotice)
                {
                    warnings.Add(writerNotice);
                }
            }
            else
            {
                cost = AnswerCost.Unknown(tokens, pack.Complete);
            }
            if (unknownBaselineFiles > 0)
            {
                warnings.Add(
                    $"{unknownBaselineFiles} file(s) not in the index — excluded from the whole-file baseline");
            }

            var data = new CodePackData(
                pack.Name,
                pack.Kind,
                pack.Repo,
                pack.File,
                pack.Span,
                pack.Signature,
                pack.ContainerName,
                pack.DocComment,
                pack.BodyLines,
                pack.BodyOmitted,
                pack.Members.Select(m => new PackMemberData(m.Name, m.Kind, m.Line)).ToList(),
                pack.MembersOmitted,
                pack.Callers,
                pack.WithheldCallers,
                pack.LookupTokens,
                pack.CallerTokens,
                pack.BudgetTokens,
                pack.LookupTruncated,
                pack.AdditionalMatches,
                pack.LaggingDeclarations.Select(l => new PackLaggingData(l.Repo, l.File, l.LaggingRows)).ToList(),
                pack.Complete,
                pack.GuardWarnings.Count == 0 ? null : pack.GuardWarnings);

            var envelope = ToolEnvelope.Wrap(data, tokens, !pack.Complete, warnings, root, staleFiles: null,
                didYouMean: pack.DidYouMean, cost: cost,
                fileFreshness: fileFreshness);
            var invalidated = _cache?.TakeInvalidationWarning();
            RecordLedger(root, parameters, envelope, sw, cacheHit: false, cacheInvalidated: invalidated is not null);
            _cache?.Put("code_pack", parameters, envelope);
            return envelope.WithWarning(invalidated);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex, _workspace);
        }
    }

    // the pack renders the same remedy the lookup would: a refused body with no member map behind it is only
    // weighable against a number, and the engine already priced it when it turned the text away
    private static string RemedyFor(SymbolPack pack) =>
        pack.RefusedBodyTokens > 0 && pack.Members.Count == 0
            ? HintText.BodyPriceRemedy(pack.RefusedBodyTokens)
            : pack.LookupHint?.ForMcp() ?? HintText.RaiseMaxTokensRemedy;

    // one write road for the fresh answer and the hit that replays it, both reading the state off the envelope
    private void RecordLedger(
        string root,
        object parameters,
        ToolEnvelope answer,
        Stopwatch sw,
        bool cacheHit,
        bool cacheInvalidated)
    {
        var pack = (CodePackData)answer.Data!;
        QueryTelemetry.Record(root, _workspace.SessionId, "code_pack", parameters,
            answer.TokensUsed, answer.Truncated, pack.Callers.Count, sw.ElapsedMilliseconds,
            cacheHit: cacheHit, indexAge: answer.IndexAge,
            client: _identity.Name ?? "unknown", clientVersion: _identity.Version,
            cacheInvalidated: cacheInvalidated,
            baselineTokens: answer.Cost?.BaselineTokens,
            // a pack that lost nothing and still says the body stayed out was answered with the member map
            mapped: !answer.Truncated && pack.BodyOmitted);
    }

    public sealed record CodePackData(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("span")] string Span,
        [property: JsonPropertyName("signature")] string? Signature,
        [property: JsonPropertyName("containerName")] string? ContainerName,
        [property: JsonPropertyName("docComment")] string? DocComment,
        [property: JsonPropertyName("bodyLines")] IReadOnlyList<string> BodyLines,
        [property: JsonPropertyName("bodyOmitted")] bool BodyOmitted,
        [property: JsonPropertyName("members")] IReadOnlyList<PackMemberData> Members,
        [property: JsonPropertyName("membersOmitted")] int MembersOmitted,
        [property: JsonPropertyName("callers")] IReadOnlyList<PackCaller> Callers,
        [property: JsonPropertyName("withheldCallers")] int WithheldCallers,
        [property: JsonPropertyName("lookupTokens")] int LookupTokens,
        [property: JsonPropertyName("callerTokens")] int CallerTokens,
        [property: JsonPropertyName("budgetTokens")] int BudgetTokens,
        [property: JsonPropertyName("lookupTruncated")] bool LookupTruncated,
        [property: JsonPropertyName("additionalMatches")] int AdditionalMatches,
        [property: JsonPropertyName("laggingDeclarations")] IReadOnlyList<PackLaggingData> LaggingDeclarations,
        [property: JsonPropertyName("complete")] bool Complete,
        [property: JsonPropertyName("guardWarnings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? GuardWarnings = null);

    public sealed record PackMemberData(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("line")] int Line);

    public sealed record PackLaggingData(
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("laggingRows")] int LaggingRows);
}
