namespace Squeezr.Core.Infra.Autostart;

public sealed class SystemdUserApplier(
    string unitDirectory,
    IAutostartProcessRunner runner,
    string unitName = WatchdUnitGenerator.DefaultUnitName) : IWatchdAutostartApplier
{
    private const string Systemctl = "systemctl";
    private const string UserScope = "--user";
    private const string UnitExtension = ".service";
    private const string WantedByTarget = "default.target";

    public string ManagedTarget => UnitPath;

    private string UnitFileName => unitName + UnitExtension;

    private string UnitPath => Path.Combine(unitDirectory, UnitFileName);

    private string WantsDirectory => Path.Combine(unitDirectory, $"{WantedByTarget}.wants");

    private string WantsLink => Path.Combine(WantsDirectory, UnitFileName);

    public async Task InstallAsync(string unitText, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(unitDirectory);
        File.WriteAllText(UnitPath, unitText);
        await RunCheckedAsync(
            [UserScope, "daemon-reload"],
            "reload the systemd user manager",
            cancellationToken);
        await RunCheckedAsync(
            [UserScope, "enable", "--now", UnitFileName],
            "enable and start the watch daemon",
            cancellationToken);
    }

    public Task<bool> IsInstalledAsync(CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(UnitPath) || WantedLinkIsOurs());

    public async Task<AutostartRemoval> UninstallAsync(CancellationToken cancellationToken)
    {
        var unitWritten = File.Exists(UnitPath);
        var linkIsOurs = WantedLinkIsOurs();
        if (!unitWritten && !linkIsOurs)
        {
            return AutostartRemoval.NothingManaged;
        }

        var warnings = new List<string>();
        if (unitWritten)
        {
            // disable is best-effort here, unlike every other tool call: it is the one step whose
            // failure would otherwise make the managed file impossible to ever take away, and the
            // bus being down is exactly the state an operator reaches for uninstall in. What the
            // tool answered is carried to the CLI instead of aborting the removal.
            try
            {
                await RunCheckedAsync(
                    [UserScope, "disable", "--now", UnitFileName],
                    "stop and unhook the watch daemon",
                    cancellationToken);
            }
            catch (AutostartException ex)
            {
                warnings.Add(ex.Message);
            }

            File.Delete(UnitPath);
        }

        if (linkIsOurs)
        {
            File.Delete(WantsLink);
        }

        await RunCheckedAsync(
            [UserScope, "daemon-reload"],
            "reload the systemd user manager",
            cancellationToken);
        return AutostartRemoval.Removed([.. warnings]);
    }

    private async Task RunCheckedAsync(IReadOnlyList<string> arguments, string action, CancellationToken cancellationToken) =>
        (await runner.RunAsync(Systemctl, arguments, cancellationToken)).EnsureSuccess(Systemctl, action);

    // systemd's own disable drops the wants link; it is removed here too, so a unit enabled by hand,
    // a half-finished install or a disable that never got an answer cannot keep resurrecting the
    // daemon. A link that does not resolve into our unit file belongs to somebody else and is never
    // touched.
    private bool WantedLinkIsOurs()
    {
        var target = new FileInfo(WantsLink).LinkTarget;
        if (target is null)
        {
            return false;
        }

        var resolved = Path.GetFullPath(Path.Combine(WantsDirectory, target));
        return string.Equals(resolved, Path.GetFullPath(UnitPath), StringComparison.Ordinal);
    }
}
