namespace Squeezr.Core.Infra.Autostart;

// Which of the generated entries bring the program back after it leaves non-zero. The supervisor is
// allowed to retire itself only where the answer is yes: an entry that fires once per logon turns a
// deliberate exit into a daemon that stays down until somebody signs in again.
// WatchdRestartPolicyTests pins every arm to the directive WatchdUnitGenerator actually writes.
public static class WatchdRestartPolicy
{
    public static bool RestartsOnUnsuccessfulExit(AutostartPlatform platform) => platform switch
    {
        AutostartPlatform.SystemdUser => true,
        AutostartPlatform.LaunchAgent => true,
        // /SC ONLOGON has no restart-on-exit semantic at all, and a platform this reader does not
        // know is refused rather than guessed at.
        _ => false,
    };
}
