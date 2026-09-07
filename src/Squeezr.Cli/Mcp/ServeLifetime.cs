using Microsoft.Extensions.Hosting;

namespace Squeezr.Cli.Mcp;

internal sealed class ServeLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;

    public CancellationToken ApplicationStopping => _stopped.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    public void MarkStarted() => _started.Cancel();

    public void StopApplication() => _stopped.Cancel();
}
