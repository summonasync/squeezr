namespace Squeezr.Cli.Infra;

/// <param name="ProgressHandler">
/// Invoked concurrently from the stdout and stderr pump tasks with raw output chunks; implementations must be thread-safe.
/// </param>
public sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    Action<string>? ProgressHandler = null);
