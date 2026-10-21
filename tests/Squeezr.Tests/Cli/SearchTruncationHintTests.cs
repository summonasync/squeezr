using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class SearchTruncationHintTests
{
    private const string Needle = "hintneedle";

    [Fact]
    public async Task Search_TextMode_Truncated_NamesTheCliKnobs()
    {
        var root = await NewTwoFileWorkspace();

        var search = await CliTestProcess.RunAsync(root, "search", Needle, "--max-tokens", "1");

        search.ExitCode.ShouldBe(0);
        search.StdOut.ShouldContain("truncated: raise --max-tokens or narrow (--repo, --prefix)");
        search.StdOut.ShouldNotContain("refine the query");
    }

    [Fact]
    public async Task Search_JsonMode_HintRidesOnlyTruncatedSummaries()
    {
        var root = await NewTwoFileWorkspace();

        var truncated = await CliTestProcess.RunAsync(root, "search", Needle, "--max-tokens", "1", "--json");
        truncated.ExitCode.ShouldBe(0);
        var summary = ParseSummary(truncated.StdOut);
        summary.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        summary.GetProperty("hint").GetString().ShouldBe("raise --max-tokens or narrow (--repo, --prefix)");

        var clean = await CliTestProcess.RunAsync(root, "search", Needle, "--json");
        clean.ExitCode.ShouldBe(0);
        var cleanSummary = ParseSummary(clean.StdOut);
        cleanSummary.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        cleanSummary.TryGetProperty("hint", out _).ShouldBeFalse();
    }

    private static JsonElement ParseSummary(string stdOut)
    {
        var last = stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
        return JsonDocument.Parse(last).RootElement.Clone();
    }

    private static async Task<string> NewTwoFileWorkspace()
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunAsync(root, "init", "--name", "hintws", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        File.WriteAllText(Path.Combine(root, "code", "Alpha.cs"), $"class Alpha {{ /* {Needle} */ }}\n");
        File.WriteAllText(Path.Combine(root, "code", "Beta.cs"), $"class Beta {{ /* {Needle} */ }}\n");
        (await CliTestProcess.RunAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);
        (await CliTestProcess.RunAsync(root, "index", "--json")).ExitCode.ShouldBe(0);
        return root;
    }
}
