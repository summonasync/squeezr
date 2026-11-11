using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Cli.Infra.Autostart;

// A unit names a program the init system execs on its own, with nobody in front of it to read a
// managed assembly or to honour a shell alias. Install therefore resolves a pair, not a path: when the
// CLI itself runs through the dotnet muxer the entry names the muxer and carries the assembly as the
// argument it loads, and when it runs from an apphost the entry names that apphost with no argument
// behind it. This guard is the last line for the cases the resolver cannot fix, and it speaks about
// the program token of that pair only — an assembly behind the muxer is a legal argument and is never
// exec'ed, while an assembly in front of it is the unlaunchable unit the guard exists to stop.
//
// The rules live in Refusal, which answers with the reason instead of throwing: 'watchd installed'
// reads the same verdict about a unit that is already on the machine, and a diagnostic has to report
// an unlaunchable program rather than die on it.
internal static class Launchability
{
    private static readonly string[] WindowsProgramExtensions = [".exe", ".com", ".bat", ".cmd"];

    internal static void Require(AutostartPlatform platform, string programPath)
    {
        var refusal = Refusal(platform, programPath);
        if (refusal is not null)
        {
            throw Refused(programPath, refusal);
        }
    }

    internal static string? Refusal(AutostartPlatform platform, string programPath)
    {
        if (!File.Exists(programPath))
        {
            return "does not exist";
        }

        if (Path.GetExtension(programPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return "is a managed assembly, not a program";
        }

        if (platform == AutostartPlatform.WindowsScheduledTask)
        {
            var extension = Path.GetExtension(programPath);
            return WindowsProgramExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                ? null
                : $"is a '{extension}' file, which cmd.exe cannot start as a program";
        }

        // Both unix branches get here on their own terms: systemd reads the unit on WSL too. The host
        // is asked only to keep the rule readable where a file mode exists — ClientCliLocator answers
        // the same question the same way, and on a Windows host the platform arm above already spoke.
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var mode = File.GetUnixFileMode(programPath);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0
            ? "does not carry the execute bit"
            : null;
    }

    private static AutostartException Refused(string programPath, string reason) =>
        new($"Cannot install autostart: '{programPath}' {reason}. The entry execs the first token of its " +
            "command, and install puts the dotnet muxer in that position whenever squeezr itself runs " +
            "through one — the assembly belongs behind it as an argument, never in front. Run 'squeezr " +
            "watchd install' from the installed squeezr binary.");
}
