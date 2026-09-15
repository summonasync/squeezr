using System.Text;

namespace Squeezr.Tests.Cli;

public sealed class FakeDocker
{
    private static readonly UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private FakeDocker(string binDir, string capturePath)
    {
        BinDir = binDir;
        CapturePath = capturePath;
    }

    public string BinDir { get; }

    public string CapturePath { get; }

    public IReadOnlyList<string> ReadCalls() => File.Exists(CapturePath) ? File.ReadAllLines(CapturePath) : [];

    public static FakeDocker Install(string psJson = "[]")
    {
        var binDir = TestDirs.NewTempDir();
        var capturePath = Path.Combine(binDir, "docker.calls");
        var script = new StringBuilder();
        script.AppendLine("#!/bin/sh");
        script.AppendLine($"printf '%s\\n' \"$*\" >> '{capturePath}'");
        script.AppendLine("case \"$*\" in");
        script.AppendLine("  \"compose version\")");
        script.AppendLine("    printf '%s\\n' 'Docker Compose version v2.29.2'");
        script.AppendLine("    exit 0");
        script.AppendLine("    ;;");
        script.AppendLine("  *\" ps --format json\")");
        script.AppendLine($"    printf '%s' '{psJson}'");
        script.AppendLine("    exit 0");
        script.AppendLine("    ;;");
        script.AppendLine("  *)");
        script.AppendLine("    exit 0");
        script.AppendLine("    ;;");
        script.AppendLine("esac");
        var binaryPath = Path.Combine(binDir, "docker");
        File.WriteAllText(binaryPath, script.ToString());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binaryPath, ExecutableMode);
        }

        return new FakeDocker(binDir, capturePath);
    }
}
