using Squeezr.Lite.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Mcp;

public sealed class LiteToolGate(AnswerSubject? subject = null)
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

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

    private T Attach<T>(T result) =>
        result is ToolEnvelope envelope
            ? (T)(object)envelope.WithSubject(_subjectBody, _crossTreeWarning)
            : result;
}
