namespace Squeezr.Core.Infra.Autostart;

public sealed class WindowsScheduledTaskApplier(
    IAutostartProcessRunner runner,
    string unitName = WatchdUnitGenerator.DefaultUnitName) : IWatchdAutostartApplier
{
    private const string Schtasks = "schtasks.exe";
    private const string TaskNameFlag = "/TN";
    private const string QueryVerb = "/Query";
    private const string XmlFlag = "/XML";
    private const string ForceFlag = "/F";

    // A task lives in the Task Scheduler store, not on the filesystem, so every answer about it has to
    // come from schtasks itself. /XML is the one form that answers the same way on every locale: the
    // element names are the task schema, while the fields of "/V /FO LIST" are translated strings a
    // reader would break on the moment the console language is not English.
    private static readonly string[] MissingTaskMarkers =
    [
        "cannot find the file",
        "does not exist",
        "cannot find the task",
    ];

    public string ManagedTarget => $"Task Scheduler task '{unitName}'";

    // The generator hands over the whole argument line already quoted at argv level, so it goes to
    // schtasks verbatim - re-quoting it here would escape the /TR token a second time. /F is this
    // layer's addition: without it a repeat install dies on the task that is already there.
    public async Task InstallAsync(string unitText, CancellationToken cancellationToken)
    {
        (await runner.RunRawAsync(Schtasks, $"{unitText} {ForceFlag}", cancellationToken))
            .EnsureSuccess(Schtasks, "create the scheduled task");
    }

    public async Task<WatchdUnitProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        AutostartProcessResult answer;
        try
        {
            answer = await runner.RunAsync(
                Schtasks,
                [QueryVerb, TaskNameFlag, unitName, XmlFlag],
                cancellationToken);
        }
        catch (AutostartException ex)
        {
            return WatchdUnitProbe.Unprobeable(
                AutostartPlatform.WindowsScheduledTask,
                $"schtasks could not be asked about the task: {ex.Message}");
        }

        if (answer.ExitCode == 0)
        {
            var xml = answer.StandardOutput;
            return new WatchdUnitProbe(
                AutostartPlatform.WindowsScheduledTask,
                WatchdUnitAnswer.Yes,
                ScheduledTaskXml.ReadEnablement(xml),
                ScheduledTaskXml.ReadFailure(xml),
                WatchdUnitAnswer.Unsupported,
                xml,
                []);
        }

        // The exit code carries no distinction here — schtasks answers 1 for a missing task and for
        // most of its other refusals — so only a reply that names a missing task reads as absent. Any
        // other refusal is "could not ask", which is what keeps an access-denied store from being
        // reported as a machine with no autostart.
        if (NamesMissingTask(answer))
        {
            return WatchdUnitProbe.Absent(AutostartPlatform.WindowsScheduledTask);
        }

        return new WatchdUnitProbe(
            AutostartPlatform.WindowsScheduledTask,
            WatchdUnitAnswer.Unknown,
            WatchdUnitAnswer.Unknown,
            WatchdUnitAnswer.Unknown,
            WatchdUnitAnswer.Unknown,
            null,
            [$"'{Schtasks} {QueryVerb} {TaskNameFlag} {unitName} {XmlFlag}' refused to answer " +
             $"(exit {answer.ExitCode}): {AutostartProcessResult.AnswerLine(
                 $"{answer.StandardError}{answer.StandardOutput}", "no reason given")}"]);
    }

    public async Task<AutostartRemoval> UninstallAsync(CancellationToken cancellationToken)
    {
        AutostartProcessResult query;
        try
        {
            query = await runner.RunAsync(Schtasks, [QueryVerb, TaskNameFlag, unitName], cancellationToken);
        }
        catch (AutostartException ex)
        {
            return AutostartRemoval.Unconfirmed(
                $"schtasks could not be asked whether the task exists, so nothing was deleted: {ex.Message}");
        }

        if (query.ExitCode != 0)
        {
            if (NamesMissingTask(query))
            {
                return AutostartRemoval.NothingManaged;
            }

            return AutostartRemoval.Unconfirmed(
                $"schtasks would not say whether the task exists, so nothing was deleted " +
                $"(exit {query.ExitCode}): {AutostartProcessResult.AnswerLine(
                    $"{query.StandardError}{query.StandardOutput}", "no reason given")}");
        }

        (await runner.RunAsync(Schtasks, ["/Delete", TaskNameFlag, unitName, ForceFlag], cancellationToken))
            .EnsureSuccess(Schtasks, "delete the scheduled task");
        return AutostartRemoval.Removed();
    }

    private static bool NamesMissingTask(AutostartProcessResult answer) =>
        MissingTaskMarkers.Any(marker =>
            answer.StandardError.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            answer.StandardOutput.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
