namespace Squeezr.Core.Infra.Autostart;

public sealed class LaunchAgentApplier(
    string agentsDirectory,
    string launchDomain,
    IAutostartProcessRunner runner,
    string unitName = WatchdUnitGenerator.LaunchAgentUnitName) : IWatchdAutostartApplier
{
    private const string Launchctl = "launchctl";
    private const string PrintVerb = "print";
    private const string NotFoundMarker = "could not find service";

    public string ManagedTarget => PlistPath;

    private string PlistFileName => $"{unitName}.plist";

    private string PlistPath => Path.Combine(agentsDirectory, PlistFileName);

    private string ServiceRef => $"{launchDomain}/{unitName}";

    // An install over an existing file writes the new plist first, then boots the loaded job out,
    // then bootstraps — a job that is loaded stays loaded through a rewrite of its file, so the
    // bootout is what makes bootstrap read the new text back. Its answer is dropped here: a plist
    // that was written but never bootstrapped legitimately reports the job as unknown, and the
    // bootstrap that follows is checked and is the step that has to succeed anyway.
    public async Task InstallAsync(string unitText, CancellationToken cancellationToken)
    {
        var replacing = File.Exists(PlistPath);
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(PlistPath, unitText);
        if (replacing)
        {
            await BootOutAsync(cancellationToken);
        }

        (await runner.RunAsync(
            Launchctl,
            ["bootstrap", launchDomain, PlistPath],
            cancellationToken)).EnsureSuccess(Launchctl, "bootstrap the launch agent");
    }

    // A plist in ~/Library/LaunchAgents is the registration: launchd reads that directory at every
    // login, so "on disk" answers enablement and print only answers whether this session has the job
    // loaded yet. launchd's own run outcome is not read here — print answers as prose, and nothing
    // about a crash loop is inferable from it without a machine to measure on, so the answer is named
    // unsupported rather than guessed at from an unmeasured format.
    public async Task<WatchdUnitProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PlistPath))
        {
            return WatchdUnitProbe.Absent(AutostartPlatform.LaunchAgent);
        }

        var (text, readNote) = ManagedUnitText.Read(PlistPath);
        var notes = new List<string>();
        if (readNote is not null)
        {
            notes.Add(readNote);
        }

        var (loaded, loadedNote) = await ReadLoadedAsync(cancellationToken);
        if (loadedNote is not null)
        {
            notes.Add(loadedNote);
        }

        return new WatchdUnitProbe(
            AutostartPlatform.LaunchAgent,
            WatchdUnitAnswer.Yes,
            WatchdUnitAnswer.Yes,
            WatchdUnitAnswer.Unsupported,
            loaded,
            text,
            [.. notes]);
    }

    public async Task<AutostartRemoval> UninstallAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PlistPath))
        {
            return AutostartRemoval.NothingManaged;
        }

        // The bootout answer is checked on this side only: a job that refuses to boot out stays
        // loaded until the next logoff, so the operator has to read that the daemon is still running
        // even though the managed plist is gone. The removal itself stands — deleting the file is what
        // ends the autostart for good.
        var warnings = new List<string>();
        try
        {
            (await BootOutAsync(cancellationToken))
                .EnsureSuccess(Launchctl, "boot out the launch agent");
        }
        catch (AutostartException ex)
        {
            warnings.Add(ex.Message);
        }

        File.Delete(PlistPath);
        return AutostartRemoval.Removed([.. warnings]);
    }

    private Task<AutostartProcessResult> BootOutAsync(CancellationToken cancellationToken) =>
        runner.RunAsync(Launchctl, ["bootout", ServiceRef], cancellationToken);

    private async Task<(WatchdUnitAnswer Loaded, string? Note)> ReadLoadedAsync(
        CancellationToken cancellationToken)
    {
        AutostartProcessResult answer;
        try
        {
            answer = await runner.RunAsync(Launchctl, [PrintVerb, ServiceRef], cancellationToken);
        }
        catch (AutostartException ex)
        {
            return (WatchdUnitAnswer.Unknown,
                $"launchctl could not be asked whether the agent is loaded: {ex.Message}");
        }

        if (answer.ExitCode == 0)
        {
            return (WatchdUnitAnswer.Yes, null);
        }

        var reply = $"{answer.StandardOutput}{answer.StandardError}";
        if (reply.Contains(NotFoundMarker, StringComparison.OrdinalIgnoreCase))
        {
            return (WatchdUnitAnswer.No,
                "the plist is registered for the next login but launchctl has no job loaded for it now");
        }

        return (WatchdUnitAnswer.Unknown,
            $"'{Launchctl} {PrintVerb} {ServiceRef}' answered exit {answer.ExitCode}: " +
            $"{AutostartProcessResult.AnswerLine(reply, "no reason given")}");
    }
}
