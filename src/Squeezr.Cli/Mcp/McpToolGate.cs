namespace Squeezr.Cli.Mcp;

public sealed class McpToolGate(Func<string?>? takeSessionNotice = null)
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public T Run<T>(Func<T> call)
    {
        _semaphore.Wait();
        try
        {
            return Attach(call(), takeSessionNotice);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return Attach(await call(cancellationToken), takeSessionNotice);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // The observer notice is read lazily and only when an answer can carry it: the first envelope of the session
    // says it, the rest answer clean. Tool error payloads are a different shape and pass through untouched.
    private static T Attach<T>(T result, Func<string?>? takeSessionNotice) =>
        result is ToolEnvelope envelope && takeSessionNotice?.Invoke() is { } notice
            ? (T)(object)envelope.WithWarning(notice)
            : result;
}
