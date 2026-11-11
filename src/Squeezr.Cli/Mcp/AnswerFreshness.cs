using System.Text.Json.Serialization;
using Squeezr.Core.Indexing;

namespace Squeezr.Cli.Mcp;

// The freshness story of one answer, stated as data: which class of check ran, how much of what the answer
// claims that check covered, how many stale positions it found and how many it actually names.
//
// The two classes never share a vocabulary. `structure` is the map's stat-only pass — a row whose file is
// gone, or whose size or mtime no longer matches the stamp; it reads no content and refreshes nothing.
// `content` is the guard's hash-checked path, which reindexes what it can and reports what it could not
// confirm. A stale count says nothing about why: a store this call could not open collapses every candidate
// into the same outcome, so an answer names coverage and counts, never that kind of cause. The one cause the
// guard owns outright is the refresh budget, and it ships as its own number — see `unrefreshedFiles`.
//
// The denominator is named after the volume it divides, because the two volumes are not the same one:
// `scopeRows` is everything a map scope speaks for (checked + withheld + skipped rows), `namedFiles` the
// distinct files a query answer names. A pass that reached nothing must not read as a clean sheet.
//
// `listedMissing` splits the list the answer carries: staleFiles runs vanished rows first and lagging rows
// after, so a reader can attribute every named file instead of reverse-engineering how the seats were
// filled. That seat policy ships in repo_map's own text — the class line, the tool description and README —
// and is deliberately not left as a note here.
//
// `freshnessTokens` prices what this story costs the caller and `tokensUsed` does not count: the staleFiles
// sample, the lines that explain the check, and this block itself, on the wire basis. `tokensUsed` stays the
// price of the content the caller's budget governs — folding the story into it would move a number three
// pins hold to the budget. `replayed` marks an answer the cache handed back: its numbers describe the call
// that produced the stored envelope, not this one, and file mtimes move no cache generation.
public sealed record AnswerFreshness(
    [property: JsonPropertyName("check")] string Check,
    [property: JsonPropertyName("checked")] int Checked,
    [property: JsonPropertyName("scopeRows"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ScopeRows = null,
    [property: JsonPropertyName("namedFiles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? NamedFiles = null,
    [property: JsonPropertyName("staleTotal")] int StaleTotal = 0,
    [property: JsonPropertyName("listed")] int Listed = 0,
    [property: JsonPropertyName("listedMissing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ListedMissing = null,
    [property: JsonPropertyName("missing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Missing = null,
    [property: JsonPropertyName("changed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Changed = null,
    [property: JsonPropertyName("laggingFiles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? LaggingFiles = null,
    [property: JsonPropertyName("unrefreshedFiles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? UnrefreshedFiles = null,
    [property: JsonPropertyName("freshnessTokens")] int FreshnessTokens = 0,
    [property: JsonPropertyName("replayed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Replayed = false)
{
    public const string Structure = "structure";
    public const string Content = "content";

    // the answer names fewer stale positions than its own check found: a reader holding this cannot treat the
    // list as the verdict, and cost.complete says so in the field the envelope already uses for "not the whole
    // story". Completeness is a claim about the verdict; the price of the story is freshnessTokens, not this
    [property: JsonIgnore]
    public bool CarriesWholeVerdict => Listed >= StaleTotal;

    public static AnswerFreshness ForStructure(
        int checkedRows,
        int scopeRows,
        int staleTotal,
        int listed,
        int listedMissing,
        int missing,
        int changed,
        IReadOnlyList<string> stalePaths,
        IReadOnlyList<string?> payloadLines) =>
        Priced(
            new AnswerFreshness(
                Structure, checkedRows, ScopeRows: scopeRows, StaleTotal: staleTotal, Listed: listed,
                ListedMissing: listedMissing, Missing: missing, Changed: changed),
            stalePaths,
            payloadLines);

    public static AnswerFreshness ForContent(
        int checkedCandidates,
        int namedFiles,
        int staleTotal,
        int listed,
        int laggingFiles,
        IReadOnlyList<string> stalePaths,
        IReadOnlyList<string?> payloadLines,
        int unrefreshedFiles = 0) =>
        Priced(
            new AnswerFreshness(
                Content, checkedCandidates, NamedFiles: namedFiles, StaleTotal: staleTotal, Listed: listed,
                LaggingFiles: laggingFiles, UnrefreshedFiles: NullWhenZero(unrefreshedFiles)),
            stalePaths,
            payloadLines);

    // an answer the cache handed back still carries the numbers of the call that made them; the mark rides in
    // the block that makes the claim, and the block's own price moves with the bytes the mark adds
    public AnswerFreshness MarkReplayed()
    {
        if (Replayed)
        {
            return this;
        }

        var marked = this with { Replayed = true };
        return marked with { FreshnessTokens = Converge(marked, FreshnessTokens - FreshnessPrice.JsonTokens(this)) };
    }

    // A stat verdict is a claim too, and it is the cheaper one: the guard refuses what it could not confirm,
    // while fileFreshness calls a file lagging on size or mtime alone. The count keeps an answer from shipping
    // lagging names beside a verdict list that reads clean.
    public static int CountLaggingFiles(IReadOnlyList<FileFreshness>? rows) =>
        rows?.Count(row => row.Verdict == FreshnessVerdict.Lagging) ?? 0;

    // The cap sits on the candidates a check may reach, never on the files an answer names, so a wide answer
    // is a partially checked one. Said only when something was left out — a covered answer needs no apology.
    public static string? CoverageWarning(int checkedCount, int namedCount) =>
        checkedCount >= namedCount
            ? null
            : $"content check covered {checkedCount} of the {namedCount} files this answer names; staleFiles " +
              "lists what it did not confirm fresh, a file the check never reached carries only the stat " +
              "verdict in fileFreshness, and laggingFiles counts the files this answer names as lagging";

    // The budget is the one reason a stale row may be named out loud: the check reached the file and the call
    // chose not to re-read it. Said only when a re-read was refused — never about rows the call never
    // examined, which arrive with an empty tally of their own and no claim attached.
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

    // the block prices its own bytes and its own bytes grow with the digits of the number they carry; the
    // loop settles upwards, so a declared price never comes back cheaper than what ships
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
