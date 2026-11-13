namespace Squeezr.Core.Infra.Autostart;

public enum WatchdAutostartState
{
    Installed,
    NotInstalled,
    InstalledButBinaryMissing,
    UnitFailed,
    PresentButNotEnabled,
    ProbeUnknown,
    ProbeUnsupported,
}
