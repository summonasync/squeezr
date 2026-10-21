using System.Diagnostics;
using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Mcp;

[Collection("ProcessGlobals")]
public sealed class McpServeProcessTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    [Fact]
    public async Task Serve_PipedSession_EveryStdoutLineIsValidJsonRpc()
    {
        var root = TestDirs.NewTempDir();

        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" serve")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)!;
        await process.StandardInput.WriteLineAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"purity-test","version":"1.0"}}}
            """);
        await process.StandardInput.WriteLineAsync("""
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            """);
        await process.StandardInput.WriteLineAsync("""
            {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
            """);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestTimeout());
        var stdOut = await stdoutTask;
        var stdErr = await stderrTask;

        process.ExitCode.ShouldBe(0);
        stdErr.ShouldContain("squeezr serve");

        var lines = stdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Length.ShouldBeGreaterThanOrEqualTo(2);

        var sawInitializeResult = false;
        var sawToolsList = false;
        foreach (var line in lines)
        {
            line.Contains('\u001b').ShouldBeFalse();
            using var doc = JsonDocument.Parse(line);
            var message = doc.RootElement;
            message.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
            var id = message.GetProperty("id").GetInt32();
            var result = message.GetProperty("result");
            if (id == 1)
            {
                sawInitializeResult = result.TryGetProperty("capabilities", out _);
            }
            else if (id == 2)
            {
                sawToolsList = result.GetProperty("tools").EnumerateArray().Count() == 6;
            }
        }

        sawInitializeResult.ShouldBeTrue();
        sawToolsList.ShouldBeTrue();
    }

    [Fact]
    public async Task Serve_SessionOutsideResolvedRoot_EnvelopeRebindsAndStderrWarningStays()
    {
        var home = TestDirs.NewTempDir();
        var registered = TestDirs.NewTempDir();
        var session = TestDirs.NewTempDir();

        await CliTestProcess.RunWithHomeAsync(home, registered, "init", "--name", "wire-root");
        File.Delete(Directory.GetFiles(
            Path.Combine(home, "workspaces"), "workspace.json", SearchOption.AllDirectories).Single());

        var (stdout, stderr) = await ServeToolCallAsync(home, session, registered,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"index_status","arguments":{}}}""");

        var answer = ToolCallResponse(stdout);
        answer.ShouldContain("\"code\":\"workspace_not_initialized\"");
        answer.ShouldContain("\"cause\":\"session_outside_root\"");
        answer.ShouldContain(Path.GetFullPath(registered));
        answer.ShouldNotContain("squeezr init");
        answer.ShouldContain("squeezr mcp install");
        answer.ShouldContain("Do not initialize a store");

        stderr.ShouldContain("is outside the workspace root");
        stderr.ShouldContain("is not an initialized workspace");
    }

    [Fact]
    public async Task Serve_SessionAtUnresolvedRoot_EnvelopePrescribesInit()
    {
        var home = TestDirs.NewTempDir();
        var session = TestDirs.NewTempDir();

        var (stdout, stderr) = await ServeToolCallAsync(home, session, null,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"index_status","arguments":{}}}""");

        var answer = ToolCallResponse(stdout);
        answer.ShouldContain("\"cause\":\"root_without_manifest\"");
        answer.ShouldContain("squeezr init");
        answer.ShouldContain("squeezr index");
        answer.ShouldContain(Path.GetFullPath(session));
        answer.ShouldNotContain("squeezr mcp install");

        stderr.ShouldNotContain("is outside the workspace root");
    }

    private static async Task<(string StdOut, string StdErr)> ServeToolCallAsync(
        string squeezrHome, string workingDir, string? claudeProjectDir, string toolCall)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" serve")
        {
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["SQUEEZR_HOME"] = squeezrHome;
        startInfo.Environment["CLAUDE_PROJECT_DIR"] = claudeProjectDir ?? string.Empty;
        startInfo.Environment["NO_COLOR"] = "1";

        using var process = Process.Start(startInfo)!;
        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"cause-probe","version":"0"}}}""");
        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await process.StandardInput.WriteLineAsync(toolCall);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestTimeout());
        return (stdout, stderr);
    }

    private static string ToolCallResponse(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(line => line.Contains("\"id\":2"));

    private static CancellationToken TestTimeout() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;
}
