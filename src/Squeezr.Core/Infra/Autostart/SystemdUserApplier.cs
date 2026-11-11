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
    private const string IsFailedVerb = "is-failed";
    private const int NotLoadedExit = 4;

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

    // Two of the four answers come off the disk and one call answers the other two, because that is as
    // far as systemd can be asked without touching anything: enablement is the wants link install
    // created, and is-failed reports both the failure state and whether the running manager has read
    // the file at all. is-enabled is deliberately not consulted — measured on systemd 255, a static
    // unit answers it with exit 0, which would read as enabled for a unit nobody hooked.
    public async Task<WatchdUnitProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var unitWritten = File.Exists(UnitPath);
        var linkIsOurs = WantedLinkIsOurs();
        if (!unitWritten)
        {
            return WatchdUnitProbe.Absent(
                AutostartPlatform.SystemdUser,
                linkIsOurs
                    ? $"an enable link at {WantsLink} points at a unit file that is gone"
                    : null);
        }

        var (text, readNote) = ManagedUnitText.Read(UnitPath);
        var notes = new List<string>();
        if (readNote is not null)
        {
            notes.Add(readNote);
        }

        var (failed, loaded, failureNote) = await ReadManagerStateAsync(cancellationToken);
        if (failureNote is not null)
        {
            notes.Add(failureNote);
        }

        if (!linkIsOurs)
        {
            notes.Add(
                $"no enable link at {WantsLink} — the unit is on disk but nothing asks systemd to start it");
        }

        return new WatchdUnitProbe(
            AutostartPlatform.SystemdUser,
            WatchdUnitAnswer.Yes,
            linkIsOurs ? WatchdUnitAnswer.Yes : WatchdUnitAnswer.No,
            failed,
            loaded,
            text,
            [.. notes]);
    }

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

    // The exit code alone cannot tell an answer from a silence: measured on systemd 255, a bus that
    // will not connect answers exit 1 with empty stdout and the reason on stderr, which is the same
    // exit code a loaded unit that has not failed answers with. The state word is the gate.
    private async Task<(WatchdUnitAnswer Failed, WatchdUnitAnswer Loaded, string? Note)> ReadManagerStateAsync(
        CancellationToken cancellationToken)
    {
        AutostartProcessResult answer;
        try
        {
            answer = await runner.RunAsync(
                Systemctl,
                [UserScope, IsFailedVerb, UnitFileName],
                cancellationToken);
        }
        catch (AutostartException ex)
        {
            return (WatchdUnitAnswer.Unknown, WatchdUnitAnswer.Unknown,
                $"systemd could not be asked whether the unit failed: {ex.Message}");
        }

        var state = answer.StandardOutput.Trim();
        if (state.Length == 0)
        {
            return (WatchdUnitAnswer.Unknown, WatchdUnitAnswer.Unknown,
                $"'{Systemctl} {UserScope} {IsFailedVerb} {UnitFileName}' answered nothing " +
                $"(exit {answer.ExitCode}): {AutostartProcessResult.AnswerLine(answer.StandardError, "no reason given")}");
        }

        return answer.ExitCode switch
        {
            0 => (WatchdUnitAnswer.Yes, WatchdUnitAnswer.Yes, null),
            1 => (WatchdUnitAnswer.No, WatchdUnitAnswer.Yes, null),
            // Exit 4 is the manager saying it has never read this file, which answers the loaded
            // question and leaves the failure question open: the word it prints beside it ('inactive'
            // was measured) is a statement about a unit it does not have, not a verdict on ours. A
            // reader that stored that word as "not failed" would be reporting an answer nobody gave.
            NotLoadedExit => (WatchdUnitAnswer.Unsupported, WatchdUnitAnswer.No,
                $"the running manager does not know {UnitFileName} (exit {NotLoadedExit}, answer '{state}') — " +
                "a daemon-reload has not read this file yet"),
            _ => (WatchdUnitAnswer.Unknown, WatchdUnitAnswer.Unknown,
                $"'{Systemctl} {IsFailedVerb}' answered '{state}' with exit {answer.ExitCode}"),
        };
    }

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
