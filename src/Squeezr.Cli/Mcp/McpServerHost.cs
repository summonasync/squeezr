using System.IO.Pipelines;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Squeezr.Cli.Mcp.Tools;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

public static class McpServerHost
{
    private static readonly TimeSpan StdinEofGrace = TimeSpan.FromSeconds(1);

    public static async Task RunStdioAsync(string workspaceRoot, CancellationToken ct, bool watch = false)
    {
        Console.Error.WriteLine($"squeezr serve: workspace '{Path.GetFullPath(workspaceRoot)}'");
        var inputPipe = new Pipe();
        var forwardTask = ForwardStdinAsync(inputPipe.Writer, ct);
        await RunAsync(inputPipe.Reader.AsStream(), Console.OpenStandardOutput(), workspaceRoot, ct, watch);
        await forwardTask;
    }

    private static async Task ForwardStdinAsync(PipeWriter writer, CancellationToken ct)
    {
        try
        {
            await Console.OpenStandardInput().CopyToAsync(writer.AsStream(), ct);
            await Task.Delay(StdinEofGrace, ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    public static async Task RunAsync(
        Stream input, Stream output, string workspaceRoot, CancellationToken ct, bool watch = false)
    {
        var workspace = new McpWorkspace(workspaceRoot);
        if (!workspace.IsWorkspace)
        {
            Console.Error.WriteLine(
                $"warning: '{workspace.Root}' is not an initialized workspace; tool calls will return an error");
        }

        var gate = new McpToolGate();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(workspace);
        var lifetime = new ServeLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "squeezr",
                    Version = VersionString()
                };
                options.ServerInstructions =
                    "Squeezr: local code index for AI agents. Hybrid exact/semantic search over indexed repositories.";
            })
            .WithStreamServerTransport(input, output)
            .WithTools(new CodeSearchTool(workspace, gate))
            .WithTools(new IndexStatusTool(workspace, gate))
            .WithTools(new SymbolLookupTool(workspace, gate))
            .WithTools(new FindReferencesTool(workspace, gate))
            .WithTools(new RepoMapTool(workspace, gate))
            .WithTools(new ExpandSpanTool(workspace, gate));

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetServices<IHostedService>().Cast<BackgroundService>().Single();
        await server.StartAsync(ct);
        var watcher = await TryStartWatchEngineAsync(workspace, watch, ct);
        lifetime.MarkStarted();
        try
        {
            await server.ExecuteTask!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (watcher is not null)
            {
                await watcher.StopAsync();
            }
        }
    }

    private static async Task<WorkspaceWatcher?> TryStartWatchEngineAsync(
        McpWorkspace workspace, bool watchRequested, CancellationToken ct)
    {
        if (!watchRequested && !ConfigRequestsWatch(workspace.Root))
        {
            return null;
        }

        if (!workspace.IsWorkspace)
        {
            Console.Error.WriteLine("warning: watch requested but workspace is not initialized; engine is off");
            return null;
        }

        try
        {
            var watcher = new WorkspaceWatcher();
            await watcher.StartAsync(workspace.Root, ct);
            Console.Error.WriteLine("squeezr serve: watch engine running");
            return watcher;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"warning: watch engine failed to start: {ex.Message}");
            return null;
        }
    }

    private static bool ConfigRequestsWatch(string workspaceRoot)
    {
        try
        {
            return ConfigFile.Load(workspaceRoot).Watch.Enabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceException or ArgumentException)
        {
            return false;
        }
    }

    private static string VersionString() =>
        typeof(McpServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
