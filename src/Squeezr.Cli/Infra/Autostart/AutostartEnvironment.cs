using System.Runtime.InteropServices;
using Squeezr.Core.Infra.Autostart;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Infra.Autostart;

// The only place in autostart where the host operating system is asked about itself: the platform
// choice, the ambient environment and the per-user directories are resolved here and handed to the
// appliers as plain parameters.
internal static class AutostartEnvironment
{
    private const string XdgConfigHomeVariable = "XDG_CONFIG_HOME";

    internal static AutostartPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? AutostartPlatform.WindowsScheduledTask
        : OperatingSystem.IsMacOS() ? AutostartPlatform.LaunchAgent
        : OperatingSystem.IsLinux() ? AutostartPlatform.SystemdUser
        : throw new AutostartException(
            $"Autostart is not supported on '{RuntimeInformation.OSDescription}'.");

    internal static IWatchdAutostartApplier CreateApplier(
        AutostartPlatform platform,
        IAutostartProcessRunner runner) =>
        platform switch
        {
            AutostartPlatform.SystemdUser => new SystemdUserApplier(SystemdUnitDirectory(), runner),
            AutostartPlatform.WindowsScheduledTask => new WindowsScheduledTaskApplier(runner),
            AutostartPlatform.LaunchAgent => new LaunchAgentApplier(LaunchAgentsDirectory(), LaunchDomain, runner),
            _ => throw new AutostartException($"Unknown autostart platform {(int)platform}."),
        };

    // Only an explicitly set SQUEEZR_HOME is carried into the unit: the default home is whatever the
    // account resolves at logon anyway, and baking it in would pin the store to this shell's profile.
    internal static string? ExplicitSqueezrHome() =>
        Environment.GetEnvironmentVariable(SqueezrHome.HomeEnvironmentVariable) is { } value &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    internal static string SystemdUnitDirectory() => Path.Combine(ConfigRoot(), "systemd", "user");

    internal static string LaunchAgentsDirectory() => Path.Combine(RequireUserHome(), "Library", "LaunchAgents");

    internal static string LaunchDomain => $"gui/{getuid()}";

    // A relative XDG_CONFIG_HOME is not a config root (systemd ignores it the same way), so it must
    // not steer the unit into whatever the working directory happens to be.
    private static string ConfigRoot() =>
        Environment.GetEnvironmentVariable(XdgConfigHomeVariable) is { } xdg && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(RequireUserHome(), ".config");

    private static string RequireUserHome() =>
        UserHome.Path ?? throw new AutostartException(
            $"No user home directory could be resolved — set {SqueezrHome.HomeEnvironmentVariable} or fix HOME.");

    [DllImport("libc", SetLastError = true)]
    private static extern uint getuid();
}
