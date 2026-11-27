using System.Text.Json.Serialization;
using Squeezr.Core.Indexing;

namespace Squeezr.Lite.Mcp;

// The freshness story of one lite answer, as counts rather than adjectives. `refreshed` is the half the
// full product's block never shipped: staleTotal alone reads "healed 4" and "refused all 7" the same way,
// and a reader holding only a refusal count cannot tell a converged store from a starved one. The budget
// refusal keeps its own number because it is the one cause the guard owns outright; every other stale
// position is a row the check reached and could not confirm.
//
// `structure` is repo_map's stat-only pass — it reads no content and refreshes nothing, so it carries no
// refreshed count at all; `content` is the guard's hash-checked path, which heals what the budget allows
// and reports what it refused.
public sealed record AnswerFreshness(
    [property: JsonPropertyName("check")] string Check,
    [property: JsonPropertyName("checked")] int Checked,
    [property: JsonPropertyName("refreshed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Refreshed = null,
    [property: JsonPropertyName("staleTotal")] int StaleTotal = 0,
    [property: JsonPropertyName("listed")] int Listed = 0,
    [property: JsonPropertyName("unrefreshedFiles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? UnrefreshedFiles = null,
    [property: JsonPropertyName("freshnessTokens")] int FreshnessTokens = 0)
{
    public const string Structure = "structure";
    public const string Content = "content";

    [property: JsonIgnore]
    public bool CarriesWholeVerdict => Listed >= StaleTotal;

    public static AnswerFreshness ForContent(
        int checkedCandidates,
        int refreshed,
        int staleTotal,
        IReadOnlyList<string> stalePaths,
        IReadOnlyList<string?> payloadLines,
        int unrefreshedFiles = 0) =>
        Priced(
            new AnswerFreshness(
                Content, checkedCandidates, refreshed, staleTotal, stalePaths.Count,
                NullWhenZero(unrefreshedFiles)),
            stalePaths,
            payloadLines);

    public static AnswerFreshness ForStructure(
        int checkedRows,
        int staleTotal,
        IReadOnlyList<string> stalePaths,
        IReadOnlyList<string?> payloadLines) =>
        Priced(
            new AnswerFreshness(Structure, checkedRows, null, staleTotal, stalePaths.Count),
            stalePaths,
            payloadLines);

    // The cap sits on the candidates a check may reach, never on the files an answer names, so a wide
    // answer is a partially checked one. Said only when something was left out.
    public static string? CoverageWarning(int checkedCount, int namedCount) =>
        checkedCount >= namedCount
            ? null
            : $"content check covered {checkedCount} of the {namedCount} files this answer names; staleFiles " +
              "lists what it did not confirm fresh";

    // The budget is the one reason a stale row may be named out loud: the check reached the file and the
    // call chose not to re-read it.
    public static string? BudgetWarning(int unrefreshedCount) =>
        unrefreshedCount == 0
            ? null
            : $"refresh budget ({RefreshBudget.MaxFilesReRead} files / {RefreshBudget.MaxWallClockMillis} ms per call) spent; " +
              $"{unrefreshedCount} stale file(s) this answer names were not re-read — staleFiles lists them " +
              "and their rows still describe the content that was indexed";

    private static int? NullWhenZero(int value) => value == 0 ? null : value;

    private static AnswerFreshness Priced(
        AnswerFreshness claim,
        IReadOnlyList<string> stalePaths,
        IReadOnlyList<string?> payloadLines)
    {
        var shipped = FreshnessPrice.JsonTokens(stalePaths);
        foreach (var line in payloadLines)
        {
            if (line is not null)
            {
                shipped += FreshnessPrice.LineTokens(line);
            }
        }

        return claim with { FreshnessTokens = Converge(claim, shipped) };
    }

    private static int Converge(AnswerFreshness claim, int shippedPieces)
    {
        var declared = shippedPieces + 1;
        for (var pass = 0; pass < 8; pass++)
        {
            var priced = shippedPieces + FreshnessPrice.JsonTokens(claim with { FreshnessTokens = declared });
            if (priced == declared)
            {
                return priced;
            }

            declared = priced;
        }

        return declared;
    }
}
