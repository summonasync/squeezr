using System.Text;

namespace Squeezr.Tests.Mcp.Onboarding;

public sealed class StatefulFakeCli
{
    private static readonly UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const string InvocationDelimiter = "===NEXT===";

    private StatefulFakeCli(string binaryPath, string capturePath)
    {
        BinaryPath = binaryPath;
        CapturePath = capturePath;
    }

    public string BinaryPath { get; }

    public string CapturePath { get; }

    public IReadOnlyList<IReadOnlyList<string>> ReadAllInvocations()
    {
        if (!File.Exists(CapturePath))
        {
            return [];
        }

        var raw = File.ReadAllText(CapturePath);
        if (raw.Length == 0)
        {
            return [];
        }

        var blocks = raw.Split(InvocationDelimiter, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<IReadOnlyList<string>>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                result.Add(lines);
            }
        }
        return result;
    }

    public static StatefulFakeCli Install(
        string binDir,
        string binaryName,
        params (int ExitCode, string Stdout, string Stderr)[] responses)
    {
        Directory.CreateDirectory(binDir);
        var binaryPath = Path.Combine(binDir, binaryName);
        var capturePath = Path.Combine(binDir, binaryName + ".invocations.log");
        var counterPath = Path.Combine(binDir, binaryName + ".counter");

        var script = new StringBuilder();
        script.AppendLine("#!/bin/sh");
        script.AppendLine($"CAPTURE='{capturePath}'");
        script.AppendLine($"COUNTER_FILE='{counterPath}'");
        script.AppendLine("COUNTER=0");
        script.AppendLine("[ -f \"$COUNTER_FILE\" ] && COUNTER=$(cat \"$COUNTER_FILE\")");
        script.AppendLine("NEW_COUNTER=$((COUNTER + 1))");
        script.AppendLine("echo $NEW_COUNTER > \"$COUNTER_FILE\"");
        script.AppendLine($"printf '%s\\n' \"{InvocationDelimiter}\" >> \"$CAPTURE\"");
        script.AppendLine("printf '%s\\n' \"$@\" >> \"$CAPTURE\"");
        script.AppendLine("case $NEW_COUNTER in");

        for (var i = 0; i < responses.Length; i++)
        {
            var (exitCode, stdout, stderr) = responses[i];
            script.AppendLine($"    {i + 1})");
            if (stdout.Length > 0)
            {
                script.AppendLine($"        printf '%s' '{Convert.ToBase64String(Encoding.UTF8.GetBytes(stdout))}' | base64 -d");
            }
            if (stderr.Length > 0)
            {
                script.AppendLine($"        printf '%s' '{Convert.ToBase64String(Encoding.UTF8.GetBytes(stderr))}' | base64 -d >&2");
            }
            script.AppendLine($"        exit {exitCode}");
            script.AppendLine("        ;;");
        }

        script.AppendLine("    *)");
        script.AppendLine("        exit 0");
        script.AppendLine("        ;;");
        script.AppendLine("esac");

        // same write-then-exec race as FakeCli: the move hands execve an inode nobody ever held open
        // for writing (tests/README.md pattern 2)
        var pendingPath = binaryPath + ".installing";
        File.WriteAllText(pendingPath, script.ToString());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(pendingPath, ExecutableMode);
        }

        File.Move(pendingPath, binaryPath, overwrite: true);
        return new StatefulFakeCli(binaryPath, capturePath);
    }
}
