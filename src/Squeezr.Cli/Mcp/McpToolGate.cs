namespace Squeezr.Cli.Mcp;

public sealed class McpToolGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public T Run<T>(Func<T> call)
    {
        _semaphore.Wait();
        try
        {
            return call();
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
            return await call(cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
