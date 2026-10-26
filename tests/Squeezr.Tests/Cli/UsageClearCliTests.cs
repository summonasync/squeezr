using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class UsageClearCliTests
{
    [Fact]
    public async Task Usage_Clear_WithoutYes_RefusesWithInvalidUsageAndKeepsTelemetry()
    {
        var home = TestDirs.NewTempDir();
        try
        {
            var workspace = await SeedWorkspace(home, 2);

            var run = await CliTestProcess.RunWithHomeAsync(home, home, "usage", "--clear", "--json");

            run.ExitCode.ShouldBe(1);
            run.StdOut.ShouldBeEmpty();
            var body = ErrorBody(run.StdErr);
            body.GetProperty("code").GetString().ShouldBe("invalid_usage");
            body.GetProperty("message").GetString()!.ShouldContain("Refusing to clear");
            body.GetProperty("message").GetString()!.ShouldContain("--yes");
            File.Exists(QueriesLog(home, workspace)).ShouldBeTrue();
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Usage_Clear_WithYes_Json_WipesEveryRegisteredStore()
    {
        var home = TestDirs.NewTempDir();
        try
        {
            var first = await SeedWorkspace(home, 3);
            var second = await SeedWorkspace(home, 2);

            var run = await CliTestProcess.RunWithHomeAsync(home, home, "usage", "--clear", "--yes", "--json");

            run.ExitCode.ShouldBe(0);
            run.StdErr.ShouldBeEmpty();
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            var payload = document.RootElement;
            payload.GetProperty("totalRemoved").GetInt32().ShouldBe(5);
            payload.GetProperty("cleared").EnumerateArray().Count().ShouldBe(2);
            payload.GetProperty("cleared").EnumerateArray()
                .All(item => item.GetProperty("recordsRemoved").GetInt32() > 0).ShouldBeTrue();
            payload.GetProperty("skipped").EnumerateArray().ShouldBeEmpty();
            File.Exists(QueriesLog(home, first)).ShouldBeFalse();
            File.Exists(QueriesLog(home, second)).ShouldBeFalse();

            var after = await CliTestProcess.RunWithHomeAsync(home, home, "usage", "--json");
            after.ExitCode.ShouldBe(0);
            after.StdOut.ShouldContain("\"records\":0");
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Usage_Clear_EmptyRegistry_NoOpWithZeroExit()
    {
        var home = TestDirs.NewTempDir();
        try
        {
            var run = await CliTestProcess.RunWithHomeAsync(home, home, "usage", "--clear", "--yes", "--json");

            run.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            document.RootElement.GetProperty("cleared").EnumerateArray().ShouldBeEmpty();
            document.RootElement.GetProperty("totalRemoved").GetInt32().ShouldBe(0);

            var human = await CliTestProcess.RunWithHomeAsync(home, home, "usage", "--clear", "--yes");
            human.ExitCode.ShouldBe(0);
            human.StdOut.ShouldContain("No registered workspaces to clear.");
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Stats_ResetAll_WithYes_SharesTheFleetCore()
    {
        var home = TestDirs.NewTempDir();
        try
        {
            var first = await SeedWorkspace(home, 3);
            var second = await SeedWorkspace(home, 1);

            var refused = await CliTestProcess.RunWithHomeAsync(home, home, "stats", "--reset", "--all", "--json");
            refused.ExitCode.ShouldBe(1);
            ErrorBody(refused.StdErr).GetProperty("code").GetString().ShouldBe("invalid_usage");

            var run = await CliTestProcess.RunWithHomeAsync(
                home, home, "stats", "--reset", "--all", "--yes", "--json");

            run.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            document.RootElement.GetProperty("totalRemoved").GetInt32().ShouldBe(4);
            File.Exists(QueriesLog(home, first)).ShouldBeFalse();
            File.Exists(QueriesLog(home, second)).ShouldBeFalse();
        }
        finally
        {
            Cleanup(home);
        }
    }

    [Fact]
    public async Task Stats_Reset_SingleWorkspace_KeepsItsShapeAndBehavior()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            (await CliTestProcess.RunWithHomeAsync(home, root, "init", "--json")).ExitCode.ShouldBe(0);
            WriteQueries(QueriesLog(home, root), 2);

            var run = await CliTestProcess.RunWithHomeAsync(home, root, "stats", "--reset", "--json");

            run.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            document.RootElement.GetProperty("reset").GetBoolean().ShouldBeTrue();
            document.RootElement.GetProperty("recordsRemoved").GetInt32().ShouldBe(2);
            QueryTelemetry.Count(root).ShouldBe(0);
        }
        finally
        {
            Cleanup(home, root);
        }
    }

    private static string QueriesLog(string home, string root)
    {
        var registryPath = Path.Combine(home, "workspaces.json");
        File.Exists(registryPath).ShouldBeTrue();
        using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
        var entry = document.RootElement.EnumerateArray()
            .Single(row => row.GetProperty("rootPath").GetString() == Path.GetFullPath(root));
        return Path.Combine(home, "workspaces", entry.GetProperty("storeKey").GetString()!, "queries.jsonl");
    }

    private static async Task<string> SeedWorkspace(string home, int records)
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunWithHomeAsync(home, root, "init", "--json")).ExitCode.ShouldBe(0);
        WriteQueries(QueriesLog(home, root), records);
        return root;
    }

    private static void WriteQueries(string logPath, int records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var lines = Enumerable.Range(0, records).Select(index =>
            $"{{\"ts\":\"2026-10-25T10:0{index}:00Z\",\"sessionId\":\"{Guid.NewGuid()}\",\"tool\":\"search\"," +
            $"\"params\":null,\"tokensUsed\":120,\"truncated\":false,\"hits\":1,\"durationMs\":5," +
            $"\"cacheHit\":false,\"client\":\"cli\"}}");
        File.WriteAllLines(logPath, lines);
    }

    private static JsonElement ErrorBody(string stdErr)
    {
        var jsonLine = stdErr
            .Split('\n')
            .Last(line => line.TrimStart().StartsWith('{'));
        using var document = JsonDocument.Parse(jsonLine);
        return document.RootElement.GetProperty("error").Clone();
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
