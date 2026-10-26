using System.Text;

namespace Squeezr.Tests.Mcp.Onboarding;

public sealed class FakeCli
{
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

    // the .argv.log line-per-argument format is load-bearing: argument-shape assertions compare
    // captured lines against the exact argv list, so `echo %*` (which collapses args into one
    // line) or any space-splitting capture would silently weaken them. The Windows capture loop
    // shares one caveat: an empty-string argument reads as end-of-argv and truncates the capture,
    // where the Unix printf prints it and continues — bridge arguments are never empty today,
    // but a future caller passing "" would silently lose the tail on Windows only
    public static FakeCli Install(
        string binDir,
        string binaryName,
        string stdout = "",
        string stderr = "",
        int exitCode = 0,
        string? blockingStatement = null)
    {
        Directory.CreateDirectory(binDir);
        var binaryPath = Path.Combine(binDir, OperatingSystem.IsWindows() ? binaryName + ".cmd" : binaryName);
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
            // the POSIX blockingStatement is not portable, so its null-ness is the only signal the
            // Windows branch consumes; ping is the waiter of choice because timeout silently no-ops
            // under redirected stdin, which is exactly how test runners launch processes
            script.AppendLine("ping -n 31 127.0.0.1 > nul");
        }
        AppendEchoLines(script, stdout, toStdErr: false);
        AppendEchoLines(script, stderr, toStdErr: true);
        script.AppendLine($"exit /b {exitCode}");
        return script.ToString();
    }

    private static void AppendEchoLines(StringBuilder script, string payload, bool toStdErr)
    {
        var prefix = toStdErr ? "1>&2 echo(" : "echo(";
        foreach (var line in payload.Replace("\r\n", "\n").Split('\n'))
        {
            script.AppendLine(prefix + EscapeBatchSpecials(line));
        }
    }

    private static string EscapeBatchSpecials(string line)
    {
        var builder = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c is '^' or '&' or '<' or '>' or '|')
            {
                builder.Append('^');
            }
            builder.Append(c);
        }
        return builder.ToString();
    }
}
