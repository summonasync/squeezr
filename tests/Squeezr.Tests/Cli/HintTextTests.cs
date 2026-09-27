using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Serialization;

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
    public void ForMcp_MapHint_KeepsTheParameterWording() =>
        MapHint.ForMcp().ShouldBe("lower depth or raise maxTokens");

    [Fact]
    public void ForCli_ParameterWithoutFlagMapping_FallsBackToItsName() =>
        new TruncationHint([new("raise", ["topK"])]).ForCli().ShouldBe("raise topK");

    [Fact]
    public void Converter_WritesHintAsOneCliFlagString()
    {
        var options = new JsonSerializerOptions(SqueezrJson.OutputOptions);
        options.Converters.Add(new TruncationHintJsonConverter());

        JsonSerializer.Serialize(MapHint, options).ShouldBe("\"lower --depth or raise --max-tokens\"");
    }
}
