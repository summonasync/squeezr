using System.Text;

namespace Squeezr.Tests.Mcp.Onboarding;

public sealed class FakeCli
{
    // the .argv.log line-per-argument format is load-bearing: argument-shape assertions compare
    // captured lines against the exact argv list, so `echo %*` (which collapses args into one
    // line) or any space-splitting capture would silently weaken them
    private static readonly UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private FakeCli(string binaryPath, string capturePath)
    {
        BinaryPath = binaryPath;
        CapturePath = capturePath;
    }

    public string BinaryPath { get; }

    public string CapturePath { get; }

    public IReadOnlyList<string> ReadCapturedArguments() =>
        File.Exists(CapturePath) ? File.ReadAllLines(CapturePath) : [];

    public static FakeCli Install(
        string binDir,
        string binaryName,
        string stdout = "",
        string stderr = "",
        int exitCode = 0,
        string? blockingStatement = null)
    {
        Directory.CreateDirectory(binDir);
        var binaryPath = Path.Combine(binDir, binaryName);
        var capturePath = Path.Combine(binDir, binaryName + ".argv.log");
        File.WriteAllText(
            binaryPath,
            OperatingSystem.IsWindows()
                ? WindowsScript(capturePath, stdout, stderr, exitCode, blockingStatement)
                : UnixScript(capturePath, stdout, stderr, exitCode, blockingStatement));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binaryPath, ExecutableMode);
        }
        return new FakeCli(binaryPath, capturePath);
    }

    private static string UnixScript(
        string capturePath, string stdout, string stderr, int exitCode, string? blockingStatement)
    {
        var script = new StringBuilder();
        script.AppendLine("#!/bin/sh");
        script.AppendLine($"printf '%s\\n' \"$@\" > '{capturePath}'");
        if (blockingStatement is not null)
        {
            script.AppendLine(blockingStatement);
        }
        if (stdout.Length > 0)
        {
            script.AppendLine($"printf '%s' '{Convert.ToBase64String(Encoding.UTF8.GetBytes(stdout))}' | base64 -d");
        }
        if (stderr.Length > 0)
        {
            script.AppendLine($"printf '%s' '{Convert.ToBase64String(Encoding.UTF8.GetBytes(stderr))}' | base64 -d >&2");
        }
        script.AppendLine($"exit {exitCode}");
        return script.ToString();
    }

    private static string WindowsScript(
        string capturePath, string stdout, string stderr, int exitCode, string? blockingStatement)
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("setlocal");
        script.AppendLine($"set \"ARGS_LOG={capturePath}\"");
        script.AppendLine("break > \"%ARGS_LOG%\"");
        script.AppendLine(":arg");
        script.AppendLine("if \"%~1\"==\"\" goto args_done");
        script.AppendLine(">> \"%ARGS_LOG%\" echo(%~1");
        script.AppendLine("shift");
        script.AppendLine("goto arg");
        script.AppendLine(":args_done");
        if (blockingStatement is not null)
        {
            script.AppendLine("timeout /t 30 /nobreak > nul");
        }
        if (stdout.Length > 0)
        {
            script.AppendLine($"echo({stdout.Replace("%", "%%")}");
        }
        if (stderr.Length > 0)
        {
            script.AppendLine($"1>&2 echo({stderr.Replace("%", "%%")}");
        }
        script.AppendLine($"exit /b {exitCode}");
        return script.ToString();
    }
}
