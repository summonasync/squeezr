using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

public sealed class HintTextTests
{
    private static readonly TruncationHint SymbolHint = new(
    [
        new("narrow the query", ["repo", "kind"]),
        new("raise", ["maxTokens"])
    ]);

    private static readonly TruncationHint MapHint = new(
    [
        new("lower", ["depth"]),
        new("raise", ["maxTokens"])
    ]);

    [Fact]
    public void ForCli_SymbolHint_NamesEveryFlag() =>
        SymbolHint.ForCli().ShouldBe("narrow the query (--repo, --kind) or raise --max-tokens");

    [Fact]
    public void ForMcp_SymbolHint_NamesParametersWithoutFlagSyntax()
    {
        var text = SymbolHint.ForMcp();

        text.ShouldBe("narrow the query (repo, kind) or raise maxTokens");
        text.ShouldNotContain("--");
    }

    [Fact]
    public void ForCli_MapHint_KeepsTheFlagWording() =>
        MapHint.ForCli().ShouldBe("lower --depth or raise --max-tokens");

    [Fact]
    public void TruncationLine_WithoutLegs_KeepsTheDialect()
    {
        HintText.TruncationLine(550, 600, "raise --max-tokens")
            .ShouldBe("truncated: 550 of 600 token ceiling — raise --max-tokens");
    }

    [Fact]
    public void TruncationLine_WithLegs_AppendsPerLegNumbersAfterTheRemedy()
    {
        HintText.TruncationLine(550, 600, "raise --max-tokens", "exact 210, semantic 340")
            .ShouldBe("truncated: 550 of 600 token ceiling — raise --max-tokens (exact 210, semantic 340)");
    }

    [Fact]
    public void ForMcp_MapHint_KeepsTheParameterWording() =>
        MapHint.ForMcp().ShouldBe("lower depth or raise maxTokens");

    [Fact]
    public void ForCli_ParameterWithoutFlagMapping_FallsBackToItsName() =>
        new TruncationHint([new("raise", ["topK"])]).ForCli().ShouldBe("raise topK");

    [Fact]
    public void ForCli_ExpandInputErrors_KeepFlagSyntax()
    {
        new ExpandInputException(ExpandInputIssue.StartLine, "startLine must be >= 1.")
            .ForCli().ShouldBe("--start-line must be >= 1.");

        new ExpandInputException(
            ExpandInputIssue.AmbiguousPath,
            "neutral",
            "src/Dup.cs",
            ["alpha:src/Dup.cs", "beta:src/Dup.cs"])
            .ForCli()
            .ShouldBe("Path 'src/Dup.cs' matches multiple repositories: alpha:src/Dup.cs, beta:src/Dup.cs. Qualify with --repo.");
    }

    [Fact]
    public void ForCli_PlainWorkspaceError_PassesTheMessageThrough() =>
        new WorkspaceException("keep me").ForCli().ShouldBe("keep me");

    [Fact]
    public void Converter_WritesHintAsOneCliFlagString()
    {
        var options = new JsonSerializerOptions(SqueezrJson.OutputOptions);
        options.Converters.Add(new TruncationHintJsonConverter());

        JsonSerializer.Serialize(MapHint, options).ShouldBe("\"lower --depth or raise --max-tokens\"");
    }
}
