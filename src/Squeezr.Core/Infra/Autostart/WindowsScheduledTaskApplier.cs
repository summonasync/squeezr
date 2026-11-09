namespace Squeezr.Core.Infra.Autostart;

public sealed class WindowsScheduledTaskApplier(
    IAutostartProcessRunner runner,
    string unitName = WatchdUnitGenerator.DefaultUnitName) : IWatchdAutostartApplier
{
    private const string Schtasks = "schtasks.exe";
    private const string TaskNameFlag = "/TN";
    private const string ForceFlag = "/F";

    public string ManagedTarget => $"Task Scheduler task '{unitName}'";

    // The generator hands over the whole argument line already quoted at argv level, so it goes to
    // schtasks verbatim - re-quoting it here would escape the /TR token a second time. /F is this
    // layer's addition: without it a repeat install dies on the task that is already there.
    public async Task InstallAsync(string unitText, CancellationToken cancellationToken)
    {
        (await runner.RunRawAsync(Schtasks, $"{unitText} {ForceFlag}", cancellationToken))
            .EnsureSuccess(Schtasks, "create the scheduled task");
    }

    public Task<bool> IsInstalledAsync(CancellationToken cancellationToken) =>
        QueryAsync(cancellationToken);

    public async Task<bool> UninstallAsync(CancellationToken cancellationToken)
    {
        if (!await QueryAsync(cancellationToken))
        {
            return false;
        }

        (await runner.RunAsync(Schtasks, ["/Delete", TaskNameFlag, unitName, ForceFlag], cancellationToken))
            .EnsureSuccess(Schtasks, "delete the scheduled task");
        return true;
    }

    // The task lives in the Task Scheduler store, not on the filesystem, so existence is a question
    // for schtasks itself; a non-zero /Query is the honest "no such task" answer.
    private async Task<bool> QueryAsync(CancellationToken cancellationToken)
    {
        var query = await runner.RunAsync(Schtasks, ["/Query", TaskNameFlag, unitName], cancellationToken);
        return query.ExitCode == 0;
    }
}
