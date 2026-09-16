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

    public static FakeDocker Install(
        string psJson = "[]",
        IReadOnlyList<string>? pullProgressLines = null,
        bool pullFailsWithoutPolicyMissing = false,
        string composeVersion = "v2.29.2")
    {
        var binDir = TestDirs.NewTempDir();
        var capturePath = Path.Combine(binDir, "docker.calls");
        var script = new StringBuilder();
        script.AppendLine("#!/bin/sh");
        script.AppendLine($"printf '%s\\n' \"$*\" >> '{capturePath}'");
        script.AppendLine("case \"$*\" in");
        script.AppendLine("  \"compose version\")");
        script.AppendLine($"    printf '%s\\n' 'Docker Compose version {composeVersion}'");
        script.AppendLine("    exit 0");
        script.AppendLine("    ;;");
        script.AppendLine("  *\" ps --format json\")");
        script.AppendLine($"    printf '%s' '{psJson}'");
        script.AppendLine("    exit 0");
        script.AppendLine("    ;;");
        script.AppendLine("  *\" pull\"*)");
        if (pullFailsWithoutPolicyMissing)
        {
            script.AppendLine("    case \"$*\" in");
            script.AppendLine("      *\"--policy missing\"*)");
            AppendPullProgress(script, pullProgressLines, "        ");
            script.AppendLine("        exit 0");
            script.AppendLine("        ;;");
            script.AppendLine("      *)");
            script.AppendLine("        printf '%s\\n' 'fake docker: registry unreachable' >&2");
            script.AppendLine("        exit 1");
            script.AppendLine("        ;;");
            script.AppendLine("    esac");
            script.AppendLine("    ;;");
        }
        else
        {
            AppendPullProgress(script, pullProgressLines, "    ");
            script.AppendLine("    exit 0");
            script.AppendLine("    ;;");
        }
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

    private static void AppendPullProgress(StringBuilder script, IReadOnlyList<string>? pullProgressLines, string indent)
    {
        if (pullProgressLines is null)
        {
            return;
        }

        foreach (var line in pullProgressLines)
        {
            script.AppendLine($"{indent}printf '%s\\n' '{line}' >&2");
            script.AppendLine($"{indent}sleep 0.2");
        }
    }
}
