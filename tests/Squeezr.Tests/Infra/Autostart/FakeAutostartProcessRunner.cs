using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

internal sealed class FakeAutostartProcessRunner(
    Func<string, IReadOnlyList<string>, AutostartProcessResult>? responder = null) : IAutostartProcessRunner
{
    internal static readonly AutostartProcessResult Ok = new(0, string.Empty, string.Empty);

    internal List<AutostartCall> Calls { get; } = [];

    public Task<AutostartProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Calls.Add(new AutostartCall(fileName, arguments, Verbatim: false));
        return Task.FromResult(responder?.Invoke(fileName, arguments) ?? Ok);
    }

    public Task<AutostartProcessResult> RunRawAsync(
        string fileName,
        string argumentLine,
        CancellationToken cancellationToken)
    {
        Calls.Add(new AutostartCall(fileName, [argumentLine], Verbatim: true));
        return Task.FromResult(responder?.Invoke(fileName, [argumentLine]) ?? Ok);
    }

    internal string[] Commands() =>
        [.. Calls.Select(call => $"{(call.Verbatim ? "raw " : string.Empty)}{call.FileName} {string.Join(' ', call.Arguments)}")];
}

internal sealed record AutostartCall(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool Verbatim);
