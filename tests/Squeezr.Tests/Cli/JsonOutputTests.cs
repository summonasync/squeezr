using System.Diagnostics;
using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class JsonOutputTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    [Fact]
    public async Task Status_Json_IncludesCountsIndexAgeAndStaleFiles()
    {
        var root = TestDirs.NewTempDir();
        await InitAndIndexAsync(root);

        var status = await RunCliAsync(root, "status", "--json");
        status.ExitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        var payload = doc.RootElement;
        payload.GetProperty("name").GetString().ShouldBe("jsonws");
        payload.GetProperty("files").GetInt32().ShouldBe(1);
        payload.GetProperty("symbols").GetInt32().ShouldBeGreaterThan(0);
        payload.GetProperty("chunks").GetInt32().ShouldBeGreaterThan(0);
        payload.GetProperty("staleFiles").GetArrayLength().ShouldBe(0);
        TimeSpan.Parse(payload.GetProperty("indexAge").GetString()!)
            .ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);

        var repo = payload.GetProperty("repos")[0];
        repo.GetProperty("name").GetString().ShouldBe("code");
        repo.GetProperty("path").GetString().ShouldBe("code");
        repo.GetProperty("files").GetInt32().ShouldBe(1);
        repo.GetProperty("staleFiles").GetArrayLength().ShouldBe(0);
        repo.GetProperty("lastIndexedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        TimeSpan.Parse(repo.GetProperty("indexAge").GetString()!)
            .ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task Status_Json_AfterChanges_ReportsStaleFiles()
    {
        var root = TestDirs.NewTempDir();
        await InitAndIndexAsync(root, "Alpha.cs", "Beta.cs");

        File.WriteAllText(Path.Combine(root, "code", "Alpha.cs"), "class Alpha { void Changed() { } }\n");
        File.Delete(Path.Combine(root, "code", "Beta.cs"));
        File.WriteAllText(Path.Combine(root, "code", "Gamma.cs"), "class Gamma { }\n");

        var status = await RunCliAsync(root, "status", "--json");
        status.ExitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        var payload = doc.RootElement;
        var stale = payload.GetProperty("staleFiles");
        stale.GetArrayLength().ShouldBe(3);
        stale[0].GetString().ShouldBe("code/Alpha.cs");
        stale[1].GetString().ShouldBe("code/Beta.cs");
        stale[2].GetString().ShouldBe("code/Gamma.cs");
        payload.GetProperty("repos")[0].GetProperty("staleFiles").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task Status_Json_OnFreshWorkspace_OmitsIndexAge()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--name", "jsonws", "--json")).ExitCode.ShouldBe(0);

        var status = await RunCliAsync(root, "status", "--json");
        status.ExitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        var payload = doc.RootElement;
        payload.GetProperty("files").GetInt32().ShouldBe(0);
        payload.GetProperty("repos").GetArrayLength().ShouldBe(0);
        payload.GetProperty("staleFiles").GetArrayLength().ShouldBe(0);
        payload.TryGetProperty("indexAge", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Search_Json_NoMatches_EmitsStandaloneParseableSummaryLine()
    {
        var root = TestDirs.NewTempDir();
        await InitAndIndexAsync(root);

        var search = await RunCliAsync(root, "search", "no_such_term_zzz", "--json");
        search.ExitCode.ShouldBe(0);

        var lines = search.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1);
        using var doc = JsonDocument.Parse(lines[0]);
        var summary = doc.RootElement;
        summary.GetProperty("type").GetString().ShouldBe("summary");
        summary.GetProperty("matches").GetInt32().ShouldBe(0);
        summary.GetProperty("tokensUsed").GetInt32().ShouldBe(0);
        summary.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        summary.TryGetProperty("maxTokens", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task WorkspaceList_Json_ListsRegisteredWorkspacesFromRegistry()
    {
        var root = TestDirs.NewTempDir();
        var name = $"jsonws-{Guid.NewGuid():N}"[..12];
        (await RunCliAsync(root, "init", "--name", name, "--json")).ExitCode.ShouldBe(0);

        var json = await RunCliAsync(root, "workspace", "list", "--json");
        json.ExitCode.ShouldBe(0);

        var lines = json.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBeGreaterThan(0);
        using var doc = JsonDocument.Parse(lines.Single(line => line.Contains(name)));
        doc.RootElement.GetProperty("name").GetString().ShouldBe(name);
        doc.RootElement.GetProperty("rootPath").GetString().ShouldBe(Path.GetFullPath(root));
        doc.RootElement.GetProperty("stale").GetBoolean().ShouldBeFalse();

        var human = await RunCliAsync(root, "workspace", "list");
        human.ExitCode.ShouldBe(0);
        human.StdOut.ShouldContain(name);
    }

    [Fact]
    public async Task JsonMode_ErrorsWithoutWorkspace_AreMachineReadableOnStdErr()
    {
        var root = TestDirs.NewTempDir();

        foreach (var args in new[]
        {
            new[] { "status", "--json" },
            new[] { "index", "--json" }
        })
        {
            var result = await RunCliAsync(root, args);
            result.ExitCode.ShouldBe(1);
            result.StdOut.ShouldBeEmpty();
            var error = ParseStdErrError(result.StdErr);
            error.GetProperty("code").GetString().ShouldBe("workspace_not_initialized");
            error.GetProperty("message").GetString()!.ShouldContain("squeezr init");
        }

        var list = await RunCliAsync(root, "workspace", "list", "--json");
        list.ExitCode.ShouldBe(0);

        var search = await RunCliAsync(root, "search", "Alpha", "--json");
        search.ExitCode.ShouldBe(1);
        search.StdOut.ShouldBeEmpty();
        var searchError = ParseStdErrError(search.StdErr);
        searchError.GetProperty("code").GetString().ShouldBe("workspace_not_initialized");
        searchError.GetProperty("message").GetString()!.ShouldContain("squeezr init");
    }

    private static JsonElement ParseStdErrError(string stdErr)
    {
        var lines = stdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var doc = JsonDocument.Parse(lines[^1]);
        return doc.RootElement.GetProperty("error").Clone();
    }

    private static async Task InitAndIndexAsync(string root, params string[] fileNames)
    {
        (await RunCliAsync(root, "init", "--name", "jsonws", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        var names = fileNames.Length == 0 ? ["Alpha.cs"] : fileNames;
        foreach (var name in names)
        {
            var typeName = Path.GetFileNameWithoutExtension(name);
            File.WriteAllText(Path.Combine(root, "code", name), $"class {typeName} {{ }}\n");
        }

        (await RunCliAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index", "--json")).ExitCode.ShouldBe(0);
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
