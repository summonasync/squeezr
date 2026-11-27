using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Squeezr.Lite.Config;
using Squeezr.Lite.Mcp.Tools;
using Squeezr.Lite.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Mcp;

public static class LiteServerHost
{
    private static readonly TimeSpan StdinEofGrace = TimeSpan.FromSeconds(1);

    public static async Task RunStdioAsync(string startDirectory, CancellationToken ct, bool watch = false)
    {
        var resolution = WorkspaceRootResolver.Resolve(startDirectory);
        if (resolution.ViaClaudeProjectDir)
        {
            Console.Error.WriteLine($"workspace root resolved via CLAUDE_PROJECT_DIR: {resolution.RootPath}");
        }

        WarnIfSessionOutsideWorkspace(resolution.RootPath, startDirectory, Console.Error);
        ServeBindingLine.Write(resolution, startDirectory, Console.Error);

        var inputPipe = new Pipe();
        var forwardTask = ForwardStdinAsync(inputPipe.Writer, ct);
        await RunAsync(
            inputPipe.Reader.AsStream(), Console.OpenStandardOutput(), resolution.RootPath, startDirectory, ct,
            watch: watch);
        await forwardTask;
    }

    internal static void WarnIfSessionOutsideWorkspace(string workspaceRoot, string sessionDirectory, TextWriter stderr)
    {
        if (SessionLocation.IsWithinRoot(workspaceRoot, sessionDirectory))
        {
            return;
        }

        stderr.WriteLine(
            $"warning: session directory '{SessionLocation.Display(sessionDirectory)}' is outside the workspace root " +
            $"'{SessionLocation.Display(workspaceRoot)}'; tools serve that workspace's index, not the current project");
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
        Stream input,
        Stream output,
        string workspaceRoot,
        string sessionDirectory,
        CancellationToken ct,
        TextWriter? stderr = null,
        bool watch = false)
    {
        var diagnostics = stderr ?? Console.Error;
        var startup = LiteConfigFile.TryLoad(workspaceRoot);
        foreach (var warning in startup.Warnings)
        {
            diagnostics.WriteLine($"warning: {warning}");
        }

        var workspace = new LiteWorkspace(workspaceRoot, sessionDirectory);
        if (!workspace.IsWorkspace)
        {
            diagnostics.WriteLine(
                $"warning: '{workspace.Root}' is not an initialized workspace; tool calls will return an error");
        }

        var gate = new LiteToolGate(workspace.Subject);
        var identity = new ClientIdentity();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(workspace);
        var lifetime = new ServeLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "sqzrlite",
                    Version = VersionString()
                };
                options.ServerInstructions =
                    "sqzrlite: index-only code navigation for AI agents. Exact search, structural map and " +
                    "symbol navigation over a local index; no embeddings, no vector store, no external services.";
            })
            .WithStreamServerTransport(input, output)
            .WithTools(new CodeSearchTool(workspace, gate, identity))
            .WithTools(new IndexStatusTool(workspace, gate, identity))
            .WithTools(new SymbolLookupTool(workspace, gate, identity))
            .WithTools(new FindReferencesTool(workspace, gate, identity))
            .WithTools(new RepoMapTool(workspace, gate, identity))
            .WithTools(new ExpandSpanTool(workspace, gate, identity))
            .WithTools(new CodePackTool(workspace, gate, identity));

        await using var provider = services.BuildServiceProvider();
        identity.Attach(provider.GetRequiredService<McpServer>());
        var server = provider.GetServices<IHostedService>().Cast<BackgroundService>().Single();
        await server.StartAsync(ct);
        var presence = await ArmWatchEngineAsync(
            workspace, watch, startup.Config?.Watch.Enabled ?? false, diagnostics, ct);
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
            if (presence is not null)
            {
                await presence.DisposeAsync();
            }
        }
    }

    // Arming happens after the server is accepting calls, so a slow claim round-trip never delays the first
    // answer; a failure to arm degrades to the no-watcher serve instead of taking queries down with it.
    private static async Task<LiteServeWatch?> ArmWatchEngineAsync(
        LiteWorkspace workspace,
        bool watchRequested,
        bool watchEnabledInConfig,
        TextWriter diagnostics,
        CancellationToken ct)
    {
        if (!watchRequested && !watchEnabledInConfig)
        {
            if (workspace.IsWorkspace)
            {
                StartupNotice.WriteNoIndexerLine(workspace.Root, diagnostics);
            }

            return null;
        }

        if (!workspace.IsWorkspace)
        {
            diagnostics.WriteLine("warning: watch requested but workspace is not initialized; engine is off");
            return null;
        }

        var presence = LiteServeWatch.For(workspace.Root, diagnostics);
        try
        {
            await presence.StartAsync(ct);
            return presence;
        }
        catch (Exception ex) when (
            ex is WorkspaceException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.WriteLine($"warning: watch engine failed to start: {ex.Message}");
            await presence.DisposeAsync();
            return null;
        }
    }

    private static string VersionString() => AppVersion.Current;
}
