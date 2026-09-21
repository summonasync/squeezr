using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class SearchIndexNotBuiltCodeTests
{
    [Fact]
    public async Task Search_WithoutIndex_ReportsIndexNotBuiltCode()
    {
        var root = TestDirs.NewTempDir();
        var init = await RunCliAsync(root, "init", "--name", "no-index", "--json");
        init.ExitCode.ShouldBe(0);

        var search = await RunCliAsync(root, "search", "anything", "--json");

        search.ExitCode.ShouldBe(1);
        search.StdOut.ShouldBeEmpty();
        var lines = search.StdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var json = JsonDocument.Parse(lines[^1]);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("index_not_built");
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
