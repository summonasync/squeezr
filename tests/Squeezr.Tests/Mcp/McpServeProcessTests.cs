using System.Diagnostics;
using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Mcp;

[Collection("CliProcessGlobals")]
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

    private static CancellationToken TestTimeout() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;
}
