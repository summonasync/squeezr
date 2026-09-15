using System.Diagnostics;
using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class IndexEventsTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    [Fact]
    public async Task Index_Events_FirstRun_EmitsStartedFilesCompletedWithPhaseProgress()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root, ("a.cs", "class Alpha { }"));

        var index = await RunCliAsync(root, "index", "--events");
        index.ExitCode.ShouldBe(0);

        var types = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseType)
            .ToList();
        types.ShouldBe(["started", "fileIndexed", "progress", "progress", "completed"]);

        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (var started = JsonDocument.Parse(lines[0]))
        {
            started.RootElement.GetProperty("workspace").GetString().ShouldBe(Path.GetFullPath(root));
        }

        using (var file = JsonDocument.Parse(lines[1]))
        {
            file.RootElement.GetProperty("repo").GetString().ShouldBe("code");
            file.RootElement.GetProperty("path").GetString().ShouldBe("a.cs");
            file.RootElement.GetProperty("files").GetInt32().ShouldBe(1);
        }

        using (var semantic = JsonDocument.Parse(lines[2]))
        {
            semantic.RootElement.GetProperty("phase").GetString().ShouldBe("semantic");
            semantic.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
        }

        using (var completed = JsonDocument.Parse(lines[4]))
        {
            completed.RootElement.GetProperty("files").GetInt32().ShouldBe(1);
            completed.RootElement.GetProperty("added").GetInt32().ShouldBe(1);
            completed.RootElement.GetProperty("symbols").GetInt32().ShouldBeGreaterThan(0);
            completed.RootElement.GetProperty("repos")[0].GetProperty("name").GetString().ShouldBe("code");
        }
    }

    [Fact]
    public async Task Index_Events_QuietIncrementalRun_EmitsExactlyStartedFilesCompleted()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root,
            ("a.txt", "alpha body"),
            ("b.txt", "beta body"),
            ("c.txt", "gamma body"));
        (await RunCliAsync(root, "index")).ExitCode.ShouldBe(0);

        var index = await RunCliAsync(root, "index", "--events");
        index.ExitCode.ShouldBe(0);

        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(5);

        using (var started = JsonDocument.Parse(lines[0]))
        {
            started.RootElement.GetProperty("type").GetString().ShouldBe("started");
        }

        var expectedPaths = new[] { "a.txt", "b.txt", "c.txt" };
        for (var i = 0; i < expectedPaths.Length; i++)
        {
            using var doc = JsonDocument.Parse(lines[1 + i]);
            var e = doc.RootElement;
            e.GetProperty("type").GetString().ShouldBe("fileIndexed");
            e.GetProperty("repo").GetString().ShouldBe("code");
            e.GetProperty("path").GetString().ShouldBe(expectedPaths[i]);
            e.GetProperty("files").GetInt32().ShouldBe(i + 1);
        }

        using (var completed = JsonDocument.Parse(lines[4]))
        {
            var e = completed.RootElement;
            e.GetProperty("type").GetString().ShouldBe("completed");
            e.GetProperty("durationMs").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
            e.GetProperty("files").GetInt32().ShouldBe(3);
            e.GetProperty("added").GetInt32().ShouldBe(0);
            e.GetProperty("modified").GetInt32().ShouldBe(0);
            e.GetProperty("repos")[0].GetProperty("name").GetString().ShouldBe("code");
        }
    }

    [Fact]
    public async Task Index_Events_ComposesWithJson_AppendingFinalResultObject()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root, ("a.txt", "alpha body"));
        (await RunCliAsync(root, "index")).ExitCode.ShouldBe(0);

        var index = await RunCliAsync(root, "index", "--events", "--json");
        index.ExitCode.ShouldBe(0);

        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(4);
        ParseType(lines[0]).ShouldBe("started");
        ParseType(lines[1]).ShouldBe("fileIndexed");
        ParseType(lines[2]).ShouldBe("completed");

        using var final = JsonDocument.Parse(lines[3]);
        final.RootElement.TryGetProperty("type", out _).ShouldBeFalse();
        final.RootElement.GetProperty("repos")[0].GetProperty("files").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Index_Events_WithoutWorkspace_EmitsTerminalErrorEventAndStderrEnvelope()
    {
        var root = TestDirs.NewTempDir();

        var index = await RunCliAsync(root, "index", "--events");
        index.ExitCode.ShouldBe(1);

        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        using (var started = JsonDocument.Parse(lines[0]))
        {
            started.RootElement.GetProperty("type").GetString().ShouldBe("started");
        }

        using (var error = JsonDocument.Parse(lines[1]))
        {
            error.RootElement.GetProperty("type").GetString().ShouldBe("error");
            error.RootElement.GetProperty("code").GetString().ShouldBe("workspace_not_initialized");
        }

        var envelope = ParseStdErrError(index.StdErr);
        envelope.GetProperty("code").GetString().ShouldBe("workspace_not_initialized");
    }

    [Fact]
    public async Task Index_WithoutEvents_KeepsPlainHumanOutput()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root, ("a.txt", "alpha body"));
        (await RunCliAsync(root, "index")).ExitCode.ShouldBe(0);

        var index = await RunCliAsync(root, "index");
        index.ExitCode.ShouldBe(0);
        index.StdOut.ShouldContain("code: +0 ~0 -0 (skipped: 0, files: 1)");
        index.StdOut.TrimStart().StartsWith('{').ShouldBeFalse();
    }

    private static string ParseType(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("type").GetString()!;
    }

    private static JsonElement ParseStdErrError(string stdErr)
    {
        var lines = stdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var doc = JsonDocument.Parse(lines[^1]);
        return doc.RootElement.GetProperty("error").Clone();
    }

    private static async Task InitAddAsync(string root, params (string Path, string Content)[] files)
    {
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        foreach (var (path, content) in files)
        {
            File.WriteAllText(Path.Combine(root, "code", path), content);
        }

        (await RunCliAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);
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
