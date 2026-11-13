namespace Squeezr.Core.Infra.Autostart;

public interface IAutostartProcessRunner
{
    Task<AutostartProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    // For a tool whose arguments are already quoted at argv level: the line is handed over
    // untouched, so nothing re-escapes it a second time.
    Task<AutostartProcessResult> RunRawAsync(
        string fileName,
        string argumentLine,
        CancellationToken cancellationToken);
}
