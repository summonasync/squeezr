using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Cli.Infra.Autostart;

// A unit names a program the init system execs on its own, with nobody in front of it to read a
// managed assembly or to honour a shell alias. DaemonBinaryPath is not an executable resolver: it
// answers with the entry assembly whenever the CLI runs through the muxer, and that file is a
// library the platform will refuse. Install therefore checks what it is about to bake in and
// refuses rather than rewriting the command with a muxer prefix — a unit that starts a different
// program than the one the operator ran is a worse lie than an error line.
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
        new($"Cannot install autostart: '{programPath}' {reason}. " +
            "Run 'squeezr watchd install' from the installed squeezr binary — the unit starts this " +
            "program directly, with no muxer in front of it.");
}
