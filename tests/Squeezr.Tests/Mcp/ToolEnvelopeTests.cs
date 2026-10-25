using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Mcp;
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
}
