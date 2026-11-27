using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;
using Squeezr.Core.Storage;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Mcp.Tools;

[McpServerToolType]
public sealed class CodePackTool
{
    private const int DefaultMaxTokens = 2000;

    private readonly LiteWorkspace _workspace;
    private readonly LiteToolGate _gate;
    private readonly ClientIdentity _identity;

    public CodePackTool(LiteWorkspace workspace, LiteToolGate gate, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _identity = identity;
    }

    [Description("Answer \"what is this symbol and who touches it\" in ONE call: definition (signature, doc comment, file:span), the defining member's body within the budget (maxTokens default 2000), every caller as file:line rows, and per-file cost — instead of chaining symbol_lookup → expand_span → find_references. Budget order: definition and body spend first, callers truncate (the withheld count is in the response). complete:false names the way out; expand_span reads the exact lines around any coordinate. Per-tool exactness stays available: symbol_lookup, find_references and expand_span keep their contracts.")]
    [McpServerTool(Name = "code_pack", Title = "Code pack")]
    public Task<object> PackAsync(
        string name,
        string? repo = null,
        string? kind = null,
        int? maxTokens = null,
        CancellationToken cancellationToken = default) =>
        _gate.RunAsync(ct => Task.FromResult(PackCore(name, repo, kind, maxTokens)), cancellationToken);

    private object PackCore(string name, string? repo, string? kind, int? maxTokens)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var budget = maxTokens ?? DefaultMaxTokens;
            var warnings = new List<string>();
            var pack = new SymbolPackComposer(root).Compose(name, repo, kind, budget);

            if (pack.File.Length == 0)
            {
                warnings.Add("No symbol found with that name.");
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
            if (pack.File.Length > 0)
            {
                var files = new List<(string Repo, string Path)> { (pack.Repo, pack.File) };
                files.AddRange(pack.Callers.Select(c => (c.Repo, c.File)));
                cost = CostBaseline.Measure(root, files, tokens, pack.Complete).Cost;
                if (WriterNotice.For(root) is { } writerNotice)
                {
                    warnings.Add(writerNotice);
                }
            }
            else
            {
                cost = AnswerCost.Unknown(tokens, pack.Complete);
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

            var envelope = ToolEnvelope.Wrap(data, tokens, !pack.Complete, warnings, root,
                didYouMean: pack.DidYouMean, cost: cost);
            QueryTelemetry.Record(root, _workspace.SessionId, "code_pack",
                new { name, repo, kind, maxTokens = budget },
                envelope.TokensUsed, envelope.Truncated, pack.Callers.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version,
                mapped: !envelope.Truncated && pack.BodyOmitted);
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex, _workspace);
        }
    }

    private static string RemedyFor(SymbolPack pack) =>
        pack.RefusedBodyTokens > 0 && pack.Members.Count == 0
            ? HintText.BodyPriceRemedy(pack.RefusedBodyTokens)
            : pack.LookupHint?.ForMcp() ?? HintText.RaiseMaxTokensRemedy;

    private sealed record CodePackData(
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

    private sealed record PackMemberData(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("line")] int Line);

    private sealed record PackLaggingData(
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("laggingRows")] int LaggingRows);
}
