using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Mcp;

public class ToolEnvelopeTests
{
    [Fact]
    public void Serialize_UsesWireContractNames()
    {
        var envelope = ToolEnvelope.Wrap(
            new { hit = 1 },
            tokensUsed: 7,
            truncated: true,
            warnings: ["w1"],
            workspaceRoot: TestDirs.NewTempDir(),
            staleFiles: ["demo/a.cs"]);

        var json = JsonSerializer.Serialize(envelope);

        json.ShouldContain("\"data\":");
        json.ShouldContain("\"tokensUsed\":7");
        json.ShouldContain("\"truncated\":true");
        json.ShouldContain("\"warnings\":[\"w1\"]");
        json.ShouldContain("\"indexAge\":null");
        json.ShouldContain("\"staleFiles\":[\"demo/a.cs\"]");
    }

    [Fact]
    public void Wrap_WithoutMetaDb_IndexAgeIsNull()
    {
        var envelope = ToolEnvelope.Wrap(null, 0, false, workspaceRoot: TestDirs.NewTempDir());

        envelope.IndexAge.ShouldBeNull();
    }

    [Fact]
    public void Wrap_WithMetaDb_IndexAgeIsPositive()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
        File.WriteAllText(SqueezrPaths.MetaDbPath(root), string.Empty);

        var envelope = ToolEnvelope.Wrap(null, 0, false, workspaceRoot: root);

        envelope.IndexAge.ShouldNotBeNull();
        envelope.IndexAge!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void Serialize_CostIsLastField_WithFiveMembersInOrder()
    {
        var envelope = ToolEnvelope.Wrap(new { hit = 1 }, 7, true, ["w1"]);

        var json = JsonSerializer.Serialize(envelope);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["data", "tokensUsed", "truncated", "warnings", "indexAge", "staleFiles", "cost"]);
        root.GetProperty("cost").EnumerateObject().Select(p => p.Name).ShouldBe(
            ["tokensUsed", "baselineTokens", "savedTokens", "ratio", "complete"]);
    }

    [Fact]
    public void Serialize_WithoutFreshness_FieldAbsent_Regression()
    {
        var envelope = ToolEnvelope.Wrap(new { hit = 1 }, 7, true);

        var json = JsonSerializer.Serialize(envelope);
        using var document = JsonDocument.Parse(json);

        document.RootElement.TryGetProperty("fileFreshness", out _).ShouldBeFalse();
    }

    [Fact]
    public void Serialize_FileFreshness_AfterCost_WithVerdictVocabulary()
    {
        var freshness = new[]
        {
            new Squeezr.Core.Indexing.FileFreshness("demo", "a.cs", "fresh", null),
            new Squeezr.Core.Indexing.FileFreshness("demo", "b.cs", "lagging", 42),
        };

        var envelope = ToolEnvelope.Wrap(new { hit = 1 }, 7, true, fileFreshness: freshness);

        var json = JsonSerializer.Serialize(envelope);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["data", "tokensUsed", "truncated", "warnings", "indexAge", "staleFiles", "cost", "fileFreshness"]);
        var rows = root.GetProperty("fileFreshness").EnumerateArray().ToList();
        rows[0].GetProperty("verdict").GetString().ShouldBe("fresh");
        rows[0].TryGetProperty("lagSeconds", out _).ShouldBeFalse();
        rows[1].GetProperty("verdict").GetString().ShouldBe("lagging");
        rows[1].GetProperty("lagSeconds").GetInt64().ShouldBe(42);
    }

    [Fact]
    public void Wrap_DefaultCost_UnknownNullsAndCompleteMatchesTruncation()
    {
        var envelope = ToolEnvelope.Wrap(new { hit = 1 }, 7, true);

        envelope.Cost.ShouldNotBeNull();
        envelope.Cost.TokensUsed.ShouldBe(7);
        envelope.Cost.BaselineTokens.ShouldBeNull();
        envelope.Cost.SavedTokens.ShouldBeNull();
        envelope.Cost.Ratio.ShouldBeNull();
        envelope.Cost.Complete.ShouldBeFalse();
    }

    [Fact]
    public void Wrap_UntruncatedZeroHits_CostIsComplete()
    {
        var envelope = ToolEnvelope.Wrap(null, 0, false);

        envelope.Truncated.ShouldBeFalse();
        envelope.Cost!.Complete.ShouldBeTrue();
        envelope.Cost.Ratio.ShouldBeNull();
    }

    [Fact]
    public void Wrap_ExplicitCost_NormalizedToEnvelopeTruncation()
    {
        var cost = AnswerCost.Measured(99, 100, true);

        var envelope = ToolEnvelope.Wrap(new { hit = 1 }, 7, true, cost: cost);

        envelope.Cost!.TokensUsed.ShouldBe(7);
        envelope.Cost.Complete.ShouldBeFalse();
        envelope.Cost.BaselineTokens.ShouldBe(100L);
    }

    [Fact]
    public void Measured_SavedIsDifference_RatioNullAtZeroTokens()
    {
        var cost = AnswerCost.Measured(40, 110, true);

        cost.BaselineTokens.ShouldBe(110L);
        cost.SavedTokens.ShouldBe(70L);
        cost.Ratio.ShouldBe(2.75);
        cost.Complete.ShouldBeTrue();

        var zero = AnswerCost.Measured(0, 110, true);
        zero.Ratio.ShouldBeNull();
    }

    [Fact]
    public void Serialize_Freshness_IsLastKey_StructureShapeKeepsItsOrder()
    {
        var envelope = ToolEnvelope.Wrap(
            new { hit = 1 }, 7, false, ["w1"], staleFiles: ["demo/a.cs", "demo/b.cs"],
            freshness: AnswerFreshness.ForStructure(
                checkedRows: 75, scopeRows: 75, staleTotal: 70, listed: 2, listedMissing: 1,
                missing: 40, changed: 30,
                stalePaths: ["demo/a.cs", "demo/b.cs"],
                payloadLines: ["repo_map answers structure, not content"]));

        var json = JsonSerializer.Serialize(envelope);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["data", "tokensUsed", "truncated", "warnings", "indexAge", "staleFiles", "cost", "freshness"]);
        root.GetProperty("freshness").EnumerateObject().Select(p => p.Name).ShouldBe(
            ["check", "checked", "scopeRows", "staleTotal", "listed", "listedMissing", "missing", "changed",
                "freshnessTokens"]);
        root.GetProperty("freshness").GetProperty("check").GetString().ShouldBe("structure");
    }

    [Fact]
    public void Serialize_Freshness_ContentShapeCarriesNoStructureVocabulary()
    {
        var envelope = ToolEnvelope.Wrap(
            new { hit = 1 }, 7, false,
            staleFiles: ["demo/a.cs"],
            freshness: AnswerFreshness.ForContent(
                checkedCandidates: 1, namedFiles: 1, staleTotal: 1, listed: 1, laggingFiles: 0,
                stalePaths: ["demo/a.cs"], payloadLines: [null]));

        var json = JsonSerializer.Serialize(envelope);
        using var document = JsonDocument.Parse(json);
        var claim = document.RootElement.GetProperty("freshness");

        claim.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["check", "checked", "namedFiles", "staleTotal", "listed", "laggingFiles", "freshnessTokens"]);
        claim.GetProperty("check").GetString().ShouldBe("content");
        claim.TryGetProperty("scopeRows", out _).ShouldBeFalse(
            "a content answer speaks for files it named, never for the rows of a scope it never walked");
    }

    [Fact]
    public void Serialize_Freshness_ReplayedMarkIsLast_AndOnlyOnAnAnswerTheCacheHandedBack()
    {
        var fresh = AnswerFreshness.ForContent(
            checkedCandidates: 1, namedFiles: 1, staleTotal: 0, listed: 0, laggingFiles: 0,
            stalePaths: [], payloadLines: [null]);
        var replayed = fresh.MarkReplayed();

        var json = JsonSerializer.Serialize(ToolEnvelope.Wrap(new { hit = 1 }, 7, false, freshness: replayed));
        using var document = JsonDocument.Parse(json);
        var claim = document.RootElement.GetProperty("freshness");

        claim.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        claim.EnumerateObject().Select(p => p.Name).Last().ShouldBe("replayed");
        var freshJson = JsonSerializer.Serialize(ToolEnvelope.Wrap(new { hit = 1 }, 7, false, freshness: fresh));
        freshJson.ShouldNotContain("replayed");
        replayed.FreshnessTokens.ShouldBe(fresh.FreshnessTokens + 7,
            "the mark is 26 wire bytes of answer, and the declared price moves with the bytes that ship");
        fresh.FreshnessTokens.ShouldBe(54);
    }

    [Fact]
    public void Serialize_WithoutFreshness_FieldAbsent_OnAggregateAnswers()
    {
        var json = JsonSerializer.Serialize(ToolEnvelope.Wrap(new { hit = 1 }, 7, false));

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("freshness", out _).ShouldBeFalse();
    }

    [Fact]
    public void Wrap_ShortVerdictSample_IsMarkedIncomplete_ThoughNothingWasTruncated()
    {
        var envelope = ToolEnvelope.Wrap(
            new { hit = 1 }, 962, false, staleFiles: ["demo/a.cs"],
            freshness: AnswerFreshness.ForStructure(
                checkedRows: 12_000, scopeRows: 12_000, staleTotal: 12_000, listed: 1, listedMissing: 1,
                missing: 0, changed: 12_000,
                stalePaths: ["demo/a.cs"], payloadLines: ["structure pass covered rows"]));

        envelope.Truncated.ShouldBeFalse("the content itself was cut by no budget");
        envelope.Cost!.Complete.ShouldBeFalse(
            "an answer carrying 1 of the 12000 verdicts it found is not the whole story it names");
    }

    [Fact]
    public void Wrap_WholeVerdictSample_StaysComplete_AndStillDeclaresWhatTheStoryCosts()
    {
        var envelope = ToolEnvelope.Wrap(
            new { hit = 1 }, 962, false, staleFiles: ["demo/a.cs", "demo/b.cs"],
            freshness: AnswerFreshness.ForStructure(
                checkedRows: 10, scopeRows: 10, staleTotal: 2, listed: 2, listedMissing: 1,
                missing: 1, changed: 1,
                stalePaths: ["demo/a.cs", "demo/b.cs"], payloadLines: [null]));

        envelope.Cost!.Complete.ShouldBeTrue();
        envelope.Freshness!.FreshnessTokens.ShouldBeGreaterThan(0,
            "completeness is a claim about the verdict; the arm the reviewer measured (staleTotal == listed) " +
            "ships its sample under a true flag, so the price has to ride beside it");
    }

    [Fact]
    public void Wrap_FreshnessPayload_IsNotFoldedIntoTokensUsed()
    {
        var plain = ToolEnvelope.Wrap(new { hit = 1 }, 962, false);
        var declared = ToolEnvelope.Wrap(
            new { hit = 1 }, 962, false, staleFiles: ["demo/a.cs"],
            freshness: AnswerFreshness.ForStructure(
                checkedRows: 10, scopeRows: 10, staleTotal: 1, listed: 1, listedMissing: 1,
                missing: 1, changed: 0,
                stalePaths: ["demo/a.cs"], payloadLines: [null]));

        // the pinned branch of the pricing decision: tokensUsed stays the price of the content the caller's
        // budget governs, so the freshness story rides as its own declared number instead of moving it
        declared.TokensUsed.ShouldBe(plain.TokensUsed);
        declared.Cost!.TokensUsed.ShouldBe(plain.TokensUsed);
        declared.TokensUsed.ShouldBeLessThanOrEqualTo(2_000);
    }

    [Fact]
    public void FreshnessTokens_AreTheWirePriceOfTheSampleTheLinesAndTheBlock()
    {
        // computed off the documented basis, not off the helper: the sample serialises to 25 bytes with four
        // quotes (45 wire, 13 tokens), the line to 41 with two (51 wire, 15), and the block that carries the
        // number prices its own digits too — the sum settles at 95, not at the 28 the pieces alone cost
        var claim = AnswerFreshness.ForStructure(
            checkedRows: 75, scopeRows: 75, staleTotal: 70, listed: 2, listedMissing: 1,
            missing: 40, changed: 30,
            stalePaths: ["demo/a.cs", "demo/b.cs"],
            payloadLines: ["repo_map answers structure, not content"]);

        claim.FreshnessTokens.ShouldBe(95);

        var envelope = ToolEnvelope.Wrap(new { hit = 1 }, 962, false, staleFiles: ["demo/a.cs", "demo/b.cs"],
            freshness: claim);
        envelope.PaidTokens.ShouldBe(1_057,
            "the number a caller pays is the budgeted content plus the story it ships with");
        envelope.Data.ShouldNotBeNull();
    }

    [Fact]
    public void CoverageWarning_FiresOnlyWhereTheCapLeftFilesUnchecked()
    {
        AnswerFreshness.CoverageWarning(50, 50).ShouldBeNull();
        AnswerFreshness.CoverageWarning(50, 48).ShouldBeNull();

        var line = AnswerFreshness.CoverageWarning(50, 60)!;
        line.ShouldStartWith("content check covered 50 of the 60 files this answer names");
        line.ShouldContain("fileFreshness");
        line.ShouldContain("laggingFiles");
    }

    [Fact]
    public void BudgetWarning_NamesTheBudgetOnlyWhereAReReadWasRefused()
    {
        AnswerFreshness.BudgetWarning(0).ShouldBeNull(
            "a call that refused nothing has nothing to say, and a collapsed store never reaches a refusal");

        var line = AnswerFreshness.BudgetWarning(3)!;
        line.ShouldStartWith(
            $"refresh budget ({RefreshBudget.MaxFilesReRead} files / {RefreshBudget.MaxWallClockMillis} ms per call) spent");
        // the wording is the finding: 'not re-read' is the budget's own reason, never a failed read, an
        // unreadable file or a row the call never examined
        line.ShouldContain("3 stale file(s) this answer names were not re-read");
        line.ShouldContain("staleFiles lists them");
    }

    [Fact]
    public void CarriesWholeVerdict_IsTheOneRuleEveryCheckedToolShares()
    {
        AnswerFreshness.ForContent(50, 60, 3, 3, 0, [], [null]).CarriesWholeVerdict.ShouldBeTrue(
            "a content answer ships its whole list even when the cap covered part of the answer");
        AnswerFreshness.ForStructure(12_000, 12_000, 12_000, 50, 25, 2, 11_998, [], [null])
            .CarriesWholeVerdict.ShouldBeFalse();
        AnswerFreshness.ForStructure(3, 3, 0, 0, 0, 0, 0, [], [null]).CarriesWholeVerdict.ShouldBeTrue(
            "nothing stale and nothing listed is a whole verdict, not a clipped one");
    }
}
