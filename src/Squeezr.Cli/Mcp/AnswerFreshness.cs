using System.Text.Json.Serialization;

namespace Squeezr.Cli.Mcp;

// The freshness story of one answer, stated as data: which class of check ran, how much of what the answer
// claims that check covered, how many stale positions it found and how many it actually names.
//
// The two classes never share a vocabulary. `structure` is the map's stat-only pass — a row whose file is
// gone, or whose size or mtime no longer matches the stamp; it reads no content and refreshes nothing.
// `content` is the guard's hash-checked path, which reindexes what it can and reports what it could not
// confirm. Neither number says why a position came back stale: a store this call could not open collapses
// every candidate into the same outcome, so an answer names coverage and counts, never a cause.
//
// `named` is the denominator the coverage claim needs. On the map it is the rows its own scope speaks for
// (checked + withheld + skipped), so a pass that reached nothing cannot read as a clean sheet; on a query
// tool it is the distinct files the answer names, of which the candidate cap may have covered fewer.
// `listedMissing` splits the list the answer carries: staleFiles runs vanished rows first and lagging rows
// after, so a reader can attribute every named file instead of reverse-engineering how the seats were filled.
public sealed record AnswerFreshness(
    [property: JsonPropertyName("check")] string Check,
    [property: JsonPropertyName("checked")] int Checked,
    [property: JsonPropertyName("named")] int Named,
    [property: JsonPropertyName("staleTotal")] int StaleTotal,
    [property: JsonPropertyName("listed")] int Listed,
    [property: JsonPropertyName("listedMissing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ListedMissing = null,
    [property: JsonPropertyName("missing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Missing = null,
    [property: JsonPropertyName("changed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Changed = null)
{
    public const string Structure = "structure";
    public const string Content = "content";

    // the answer names fewer stale positions than its own check found: a reader holding this cannot treat the
    // list as the verdict, and cost.complete says so in the field the envelope already uses for "not the whole
    // story" — the unpriced bytes of the payload are the price of that honesty, not a separate claim
    [property: JsonIgnore]
    public bool CarriesWholeVerdict => Listed >= StaleTotal;

    public static AnswerFreshness ForStructure(
        int checkedRows,
        int namedRows,
        int staleTotal,
        int listed,
        int listedMissing,
        int missing,
        int changed) =>
        new(Structure, checkedRows, namedRows, staleTotal, listed, listedMissing, missing, changed);

    public static AnswerFreshness ForContent(
        int checkedCandidates,
        int namedFiles,
        int staleTotal,
        int listed) =>
        new(Content, checkedCandidates, namedFiles, staleTotal, listed);

    // The cap sits on the candidates a check may reach, never on the files an answer names, so a wide answer
    // is a partially checked one. Said only when something was left out — a covered answer needs no apology.
    public static string? CoverageWarning(int checkedCount, int namedCount) =>
        checkedCount >= namedCount
            ? null
            : $"content check covered {checkedCount} of the {namedCount} files this answer names; staleFiles " +
              "lists what it did not confirm fresh, and a file the check never reached carries only the " +
              "stat verdict in fileFreshness";
}
