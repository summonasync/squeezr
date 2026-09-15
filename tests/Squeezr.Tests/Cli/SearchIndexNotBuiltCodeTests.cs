using System.Diagnostics;
using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class SearchIndexNotBuiltCodeTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

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

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(string workingDir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" {string.Join(" ", args)}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
