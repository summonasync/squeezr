using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public class SearchCollapseRenderTests
{
    private const string Needle = "collapsedneedle";

    [Fact]
    public void CollapseNote_WordsTheSpanCount_AndStaysSilentWhenNothingCollapsed()
    {
        Match(0).CollapseNote().ShouldBeNull();
        Match(1).CollapseNote().ShouldBe("also matched 1 more span in this file");
        Match(3).CollapseNote().ShouldBe("also matched 3 more spans in this file");
    }

    [Fact]
    public void SearchMatchLine_CarriesTheCountOnlyWhenSpansWereCollapsed()
    {
        SearchMatchLine.FromMatch(Match(0)).AlsoMatched.ShouldBeNull();
        SearchMatchLine.FromMatch(Match(4)).AlsoMatched.ShouldBe(4);
    }

    [Fact]
    public async Task Search_Json_SingleSpanHit_OmitsAlsoMatched()
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunAsync(root, "init", "--name", "dedupws", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        File.WriteAllText(Path.Combine(root, "code", "Alpha.cs"), $"class Alpha {{ /* {Needle} */ }}\n");
        (await CliTestProcess.RunAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);
        (await CliTestProcess.RunAsync(root, "index", "--json")).ExitCode.ShouldBe(0);

        var search = await CliTestProcess.RunAsync(root, "search", Needle, "--json");
        search.ExitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(search.StdOut.Trim().Split('\n')[0]);
        var hit = doc.RootElement;
        hit.GetProperty("type").GetString().ShouldBe("match");
        hit.GetProperty("path").GetString().ShouldBe("Alpha.cs");
        hit.TryGetProperty("alsoMatched", out _).ShouldBeFalse();
    }

    private static SearchMatch Match(int alsoMatched) =>
        new("demo", "docs/Guide.md", 12, 0.5, "snippet") { AlsoMatched = alsoMatched };
}
