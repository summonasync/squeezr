using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Mcp;

internal sealed class McpTestHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _serverCts;
    private readonly Task _serverTask;

    private McpTestHarness(McpClient client, Task serverTask, CancellationTokenSource serverCts)
    {
        Client = client;
        _serverTask = serverTask;
        _serverCts = serverCts;
    }

    public McpClient Client { get; }

    public static async Task<McpTestHarness> StartAsync(
        string workspaceRoot, bool watch = false, string? sessionDirectory = null, WatchOwnerClaim? claimOverride = null,
        bool embedWatch = true, WatchEmbeddingRuntime? embeddingRuntime = null, TextWriter? stderr = null)
    {
        var toServer = new Pipe();
        var fromServer = new Pipe();
        var serverCts = new CancellationTokenSource();

        var serverTask = McpServerHost.RunAsync(
            toServer.Reader.AsStream(),
            fromServer.Writer.AsStream(),
            workspaceRoot,
            sessionDirectory ?? workspaceRoot,
            serverCts.Token,
            watch,
            events: false,
            claimOverride,
            embedWatch,
            embeddingRuntime is null ? null : Task.FromResult<WatchEmbeddingRuntime?>(embeddingRuntime),
            stderr);

        var transport = new StreamClientTransport(
            toServer.Writer.AsStream(),
            fromServer.Reader.AsStream(),
            NullLoggerFactory.Instance);

        var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "squeezr-tests", Version = "0.0.0" }
            },
            NullLoggerFactory.Instance);

        return new McpTestHarness(client, serverTask, serverCts);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        _serverCts.Cancel();
        await _serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        _serverCts.Dispose();
    }
}
