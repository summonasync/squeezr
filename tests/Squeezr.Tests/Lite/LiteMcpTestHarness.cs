using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Squeezr.Lite.Mcp;

namespace Squeezr.Tests.Lite;

internal sealed class LiteMcpTestHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _serverCts;
    private readonly Task _serverTask;

    private LiteMcpTestHarness(McpClient client, Task serverTask, CancellationTokenSource serverCts)
    {
        Client = client;
        _serverTask = serverTask;
        _serverCts = serverCts;
    }

    public McpClient Client { get; }

    public static async Task<LiteMcpTestHarness> StartAsync(
        string workspaceRoot, string? sessionDirectory = null, TextWriter? stderr = null)
    {
        var toServer = new Pipe();
        var fromServer = new Pipe();
        var serverCts = new CancellationTokenSource();

        var serverTask = LiteServerHost.RunAsync(
            toServer.Reader.AsStream(),
            fromServer.Writer.AsStream(),
            workspaceRoot,
            sessionDirectory ?? workspaceRoot,
            serverCts.Token,
            stderr);

        var transport = new StreamClientTransport(
            toServer.Writer.AsStream(),
            fromServer.Reader.AsStream(),
            NullLoggerFactory.Instance);

        var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqzrlite-tests", Version = "0.0.0" }
            },
            NullLoggerFactory.Instance);

        return new LiteMcpTestHarness(client, serverTask, serverCts);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        _serverCts.Cancel();
        await _serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        _serverCts.Dispose();
    }
}
