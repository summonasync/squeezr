using System.Text.RegularExpressions;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Semantic.Embeddings;

namespace Squeezr.Tests.Embeddings;

public partial class EmbeddingProviderServicesTests
{
    private static readonly Dictionary<string, bool> Verdicts = new(StringComparer.Ordinal)
    {
        ["ollama"] = true,
        ["auto"] = true,
        ["openai"] = false
    };

    [Theory]
    [InlineData("ollama", true)]
    [InlineData("auto", true)]
    [InlineData("openai", false)]
    public void RequiresManagedService_KnownProvider_AnswersFromTheTable(string provider, bool expected) =>
        EmbeddingProviderServices.RequiresManagedService(provider).ShouldBe(expected);

    [Theory]
    [InlineData("OLLAMA", true)]
    [InlineData("  Auto  ", true)]
    [InlineData(" OpenAI", false)]
    public void RequiresManagedService_CaseAndPadding_MatchTheFactoryNormalization(string provider, bool expected) =>
        EmbeddingProviderServices.RequiresManagedService(provider).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("llamafile")]
    [InlineData("ollama2")]
    public void RequiresManagedService_UnknownOrMissingProvider_OmitsTheStep(string? provider) =>
        EmbeddingProviderServices.RequiresManagedService(provider).ShouldBeFalse();

    [Fact]
    public void TableCoversEveryProviderNameTheRuntimeSwitchesOn()
    {
        var named = RuntimeProviderNames();

        named.ShouldNotContain("onnx");
        named.ShouldContain("ollama");
        named.ShouldContain("openai");
        named.ShouldContain("auto");
        named.ShouldAllBe(provider => Verdicts.ContainsKey(provider));
        named.ShouldAllBe(provider =>
            EmbeddingProviderServices.RequiresManagedService(provider) == Verdicts[provider]);
    }

    [Fact]
    public void TheTwoServiceBackedVerdictsAreTheOnlyOnes() =>
        Verdicts.Where(pair => pair.Value).Select(pair => pair.Key).ToList()
            .ShouldBe(["ollama", "auto"]);

    [Theory]
    [InlineData("""case "bge":""", "bge")]
    [InlineData("""case "bge" or "bge-int8":""", "bge,bge-int8")]
    [InlineData("""case "auto" or "ollama" or "openai":""", "auto,ollama,openai")]
    [InlineData("""case "bge": case "ollama":""", "bge,ollama")]
    [InlineData("case \"bge\"\n                or \"openai\":", "bge,openai")]
    [InlineData("""case "ollama" or "auto" when endpoint.StartsWith("http"):""", "ollama,auto")]
    [InlineData("""case ProviderName when raw.Length > 0:""", "")]
    [InlineData("""Default: throw new WorkspaceException("Supported providers: ollama.");""", "")]
    public void SwitchLabels_EveryCaseHeadForm_ReadsEachNameOnce(string source, string expected) =>
        SwitchLabels(source).ToList()
            .ShouldBe(expected.Split(',', StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void RuntimeProviderNames_OverTheTwoSwitchSources_IsExactlyTheVerdictTable() =>
        RuntimeProviderNames().ShouldBe(["openai", "ollama", "auto"], ignoreOrder: true);

    private static string Read(string project, string file) => File.ReadAllText(Path.Combine(
        FindRepoRoot(), "src", project, "Embeddings", file));

    private static List<string> RuntimeProviderNames() =>
        SwitchLabels(Read("Squeezr.Cli", "EmbeddingProviderFactory.cs"))
            .Concat(SwitchLabels(Read("Squeezr.Semantic", "EmbeddingProviderResolver.cs")))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> SwitchLabels(string source)
    {
        foreach (Match head in CaseHead().Matches(source))
        {
            foreach (Match name in QuotedName().Matches(head.Groups[1].Value))
            {
                yield return name.Groups[1].Value;
            }
        }
    }

    [GeneratedRegex(@"\bcase\s+((?:""[^""]*""\s+or\s+)*""[^""]*"")")]
    private static partial Regex CaseHead();

    [GeneratedRegex(@"""([^""]*)""")]
    private static partial Regex QuotedName();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Squeezr.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
