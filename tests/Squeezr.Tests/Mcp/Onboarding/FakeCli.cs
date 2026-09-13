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
        File.WriteAllText(binaryPath, script.ToString());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binaryPath, ExecutableMode);
        }
        return new FakeCli(binaryPath, capturePath);
    }
}
