using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

public sealed class McpToolGate(
    Func<string?>? takeSessionNotice = null,
    AnswerSubject? subject = null)
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // The subject is a property of the session, not of the answer: one serve process describes one store, so the
    // facts are read once at startup and stamped on every envelope that leaves this gate. A single-root session
    // has no subject to read, and for it this stamp is a no-op — the JSON an old consumer parses is byte-for-byte
    // what it parsed before (035).
    private readonly AnswerSubjectBody? _subjectBody = AnswerSubjectNotice.BodyOf(subject);
    private readonly string? _crossTreeWarning = AnswerSubjectNotice.CrossTreeWarningOf(subject);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return Attach(await call(cancellationToken));
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // The observer notice is read lazily and only when an answer can carry it: the first envelope of the session
    // says it, the rest answer clean. Tool error payloads are a different shape and pass through untouched.
    private T Attach<T>(T result) =>
        result is ToolEnvelope envelope
            ? (T)(object)envelope
                .WithWarning(takeSessionNotice?.Invoke())
                .WithSubject(_subjectBody, _crossTreeWarning)
            : result;
}
