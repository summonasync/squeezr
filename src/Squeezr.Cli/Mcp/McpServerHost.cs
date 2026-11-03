using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Cli.Mcp.Tools;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

public static class McpServerHost
{
    private static readonly TimeSpan StdinEofGrace = TimeSpan.FromSeconds(1);

    public static async Task RunStdioAsync(
        string startDirectory, CancellationToken ct, bool watch = false, bool events = false, bool embedWatch = true)
    {
        var resolution = WorkspaceRootResolver.Resolve(startDirectory);
        if (resolution.ViaClaudeProjectDir)
        {
            Console.Error.WriteLine($"workspace root resolved via CLAUDE_PROJECT_DIR: {resolution.RootPath}");
        }

        WarnIfSessionOutsideWorkspace(resolution.RootPath, startDirectory, Console.Error);
        Console.Error.WriteLine($"squeezr serve: workspace '{resolution.RootPath}'");
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        StaleRegistrationNudge.TryWrite(Console.Error, homeDir, Environment.ProcessPath);
        var inputPipe = new Pipe();
        var forwardTask = ForwardStdinAsync(inputPipe.Writer, ct);
        await RunAsync(
            inputPipe.Reader.AsStream(), Console.OpenStandardOutput(), resolution.RootPath, startDirectory, ct,
            watch, events, claimOverride: null, embedWatch: embedWatch);
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

    public static Task RunAsync(
        Stream input,
        Stream output,
        string workspaceRoot,
        string sessionDirectory,
        CancellationToken ct,
        bool watch = false,
        bool events = false)
    {
        return RunAsync(input, output, workspaceRoot, sessionDirectory, ct, watch, events, claimOverride: null);
    }

    // Test-only injection points, mirroring the claim seam: one process cannot hold two claims by pid, so
    // multi-participant scenarios hand in a claim built with the claim's seam-only ctor, and drain scenarios
    // hand in an already-resolved embedding runtime so tests never construct a real provider. Production
    // callers pass null and the host builds the runtime itself.
    internal static async Task RunAsync(
        Stream input,
        Stream output,
        string workspaceRoot,
        string sessionDirectory,
        CancellationToken ct,
        bool watch,
        bool events,
        WatchOwnerClaim? claimOverride,
        bool embedWatch = true,
        Task<WatchEmbeddingRuntime?>? embeddingRuntime = null,
        TextWriter? stderr = null)
    {
        var diagnostics = stderr ?? Console.Error;
        var startupConfig = StartupConfig.TryRead(workspaceRoot);
        var workspace = new McpWorkspace(workspaceRoot, sessionDirectory, startupConfig?.Embeddings.Provider);
        if (!workspace.IsWorkspace)
        {
            diagnostics.WriteLine(
                $"warning: '{workspace.Root}' is not an initialized workspace; tool calls will return an error");
        }

        var gate = new McpToolGate(workspace.TakeWatchNotice);
        var cache = new ToolResponseCache(workspaceRoot);
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
                    Name = "squeezr",
                    Version = VersionString()
                };
                options.ServerInstructions =
                    "Squeezr: local code index for AI agents. Hybrid exact/semantic search over indexed repositories.";
            })
            .WithStreamServerTransport(input, output)
            .WithTools(new CodeSearchTool(workspace, gate, cache, identity))
            .WithTools(new IndexStatusTool(workspace, gate, identity))
            .WithTools(new SymbolLookupTool(workspace, gate, cache, identity))
            .WithTools(new FindReferencesTool(workspace, gate, cache, identity))
            .WithTools(new RepoMapTool(workspace, gate, cache, identity))
            .WithTools(new ExpandSpanTool(workspace, gate, identity))
            .WithTools(new CodePackTool(workspace, gate, cache, identity));

        await using var provider = services.BuildServiceProvider();
        identity.Attach(provider.GetRequiredService<McpServer>());
        var server = provider.GetServices<IHostedService>().Cast<BackgroundService>().Single();
        await server.StartAsync(ct);
        var embedDrainReason = ResolveEmbedDrainReason(embedWatch, startupConfig?.Watch.EmbedPending);
        var presence = await TryStartWatchEngineAsync(
            workspace, watch, startupConfig?.Watch.Enabled ?? false, ct, events, claimOverride,
            embedDrainReason, embeddingRuntime, diagnostics);
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

    private static async Task<ServeWatchPresence?> TryStartWatchEngineAsync(
        McpWorkspace workspace, bool watchRequested, bool watchEnabledInConfig, CancellationToken ct, bool events,
        WatchOwnerClaim? claimOverride, string? embedDrainReason,
        Task<WatchEmbeddingRuntime?>? embeddingRuntime, TextWriter stderr)
    {
        if (!watchRequested && !watchEnabledInConfig)
        {
            if (events)
            {
                stderr.WriteLine("warning: --events has no effect: watch engine is off (pass --watch or set watch.enabled)");
            }

            // A plain serve used to be the silent case: the registration shipped --watch, so every session either
            // owned the loop or watched someone else own it. With the default flipped (070/T5), a store can now
            // legitimately run with no indexer at all, and an agent must not read that as a fresh index.
            if (workspace.IsWorkspace)
            {
                var health = WatchStoreHealth.Probe(SqueezrPaths.StoreDir(workspace.Root), DateTime.UtcNow);
                if (!health.IndexerRunning)
                {
                    var supervised = NoIndexerNotice.WatchdSupervises(workspace.Root);
                    workspace.WatchNotice = NoIndexerNotice.ForAgent(health, supervised);
                    stderr.WriteLine(NoIndexerNotice.StartupLine(health, supervised));
                }
            }

            return null;
        }

        if (!workspace.IsWorkspace)
        {
            stderr.WriteLine("warning: watch requested but workspace is not initialized; engine is off");
            return null;
        }

        try
        {
            var announcer = new ServeWatchAnnouncer(stderr);
            // The claim owns the only honest test of whether its own shutdown cleanup failed: Release answers false
            // both when the marker stayed undeletable (a wedged store for the next contender) and when the marker
            // simply is not ours anymore, so the bool at the call sites cannot carry the signal and the sink can.
            var claim = claimOverride
                ?? new WatchOwnerClaim(
                    SqueezrPaths.StoreDir(workspace.Root), DaemonHeartbeat.ServeWatchKind, announcer.Fault);
            var outcome = claim.TryAcquire();
            if (outcome.Status == WatchOwnerStatus.RaceLost)
            {
                outcome = claim.TryAcquire();
            }

            var role = ServeWatchRole.FromOutcome(outcome);
            if (!role.IsOwner)
            {
                workspace.WatchNotice = role.AgentNotice;
                announcer.Announce(role);
                // An observer indexes nothing, but it is a live process on this store holding memory and answers:
                // publishing its own beat is what lets the fleet count it. It touches no per-kind heartbeat — that
                // file belongs to the claim owner alone, and an owner demoted mid-run stops stamping it and drops
                // it the same way (070/T5), so a pre-070 reader is never handed a non-indexer as the indexer.
                var observerBeat = new DaemonHeartbeatWriter(
                    SqueezrPaths.StoreDir(workspace.Root),
                    DaemonHeartbeat.ServeWatchKind,
                    onWarning: announcer.Fault,
                    publishKindHeartbeat: false);
                observerBeat.Start();
                // The observer keeps asking for the claim while it serves: when the recorded owner dies, the next
                // acquire takes the store through the same stale rules every start obeys, and the pending
                // deferred embeddings move again without a manual 'squeezr fleet stop'.
                var presence = ServeWatchPresence.Observing(observerBeat);
                var adopter = new WatchClaimAdopter(
                    claim,
                    outcome => ArmAdoptedEngineAsync(
                        workspace, claim, announcer, ct, events, embedDrainReason, embeddingRuntime, stderr,
                        outcome, watcher => presence.Promote(watcher)),
                    onWarning: message => stderr.WriteLine($"warning: serve watch: {message}"));
                presence.AdoptClaimsThrough(adopter);
                adopter.Start();
                return presence;
            }

            var runtimeTask = ResolveEmbeddingRuntime(embedDrainReason, embeddingRuntime, workspace.Root, stderr);
            var watcher = BuildWatcher(workspace.Root, events, stderr, claim, runtimeTask);

            try
            {
                await watcher.StartAsync(workspace.Root, ct, daemonKind: DaemonHeartbeat.ServeWatchKind);
            }
            catch
            {
                claim.Release();
                throw;
            }

            stderr.WriteLine("squeezr serve: watch engine running");
            announcer.Announce(role);
            return ServeWatchPresence.Indexing(watcher);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            stderr.WriteLine($"warning: watch engine failed to start: {ex.Message}");
            return null;
        }
    }

    private static WorkspaceWatcher BuildWatcher(
        string workspaceRoot, bool events, TextWriter stderr, WatchOwnerClaim claim,
        Task<WatchEmbeddingRuntime?>? runtimeTask)
    {
        if (events)
        {
            var writer = new IndexEventWriter(stderr);
            var renderer = new IndexEventsRenderer(writer);
            return new WorkspaceWatcher(
                new WatchEventsStatus(writer, workspaceRoot, renderer), renderer,
                StaleLockWarningSink.ToStderr, claim, runtimeTask);
        }

        return new WorkspaceWatcher(
            new ServeWatchStatus(stderr), onStaleTakeover: StaleLockWarningSink.ToStderr, claim: claim,
            embeddings: runtimeTask);
    }

    private static Task<WatchEmbeddingRuntime?>? ResolveEmbeddingRuntime(
        string? embedDrainReason, Task<WatchEmbeddingRuntime?>? embeddingRuntime, string workspaceRoot,
        TextWriter stderr)
    {
        if (embedDrainReason is not null)
        {
            stderr.WriteLine($"squeezr serve: watch embed drain off ({embedDrainReason})");
            return null;
        }

        return embeddingRuntime ?? Task.Run(
            () => CreateWatchEmbeddingRuntime(workspaceRoot, stderr), CancellationToken.None);
    }

    // The observer's adoption paying off: the claim already moved, so this is the owner path again — resolve the
    // runtime, arm the engine, and only then hand it to the presence. A promote refused by a shutting-down serve
    // leaves the engine nobody's, so it stops here and the claim goes back for the next contender.
    private static async Task ArmAdoptedEngineAsync(
        McpWorkspace workspace,
        WatchOwnerClaim claim,
        ServeWatchAnnouncer announcer,
        CancellationToken ct,
        bool events,
        string? embedDrainReason,
        Task<WatchEmbeddingRuntime?>? embeddingRuntime,
        TextWriter stderr,
        WatchOwnerOutcome outcome,
        Func<WorkspaceWatcher, bool> promote)
    {
        var runtimeTask = ResolveEmbeddingRuntime(embedDrainReason, embeddingRuntime, workspace.Root, stderr);
        var watcher = BuildWatcher(workspace.Root, events, stderr, claim, runtimeTask);
        try
        {
            await watcher.StartAsync(workspace.Root, ct, daemonKind: DaemonHeartbeat.ServeWatchKind);
        }
        catch
        {
            claim.Release();
            throw;
        }

        if (!promote(watcher))
        {
            await watcher.StopAsync();
            claim.Release();
            return;
        }

        workspace.WatchNotice = null;
        stderr.WriteLine(
            outcome.TookOverFrom is { } dead
                ? $"squeezr serve: watch engine running (claim adopted from dead pid {dead.Pid})"
                : "squeezr serve: watch engine running (claim taken over, store had no owner)");
        announcer.Announce(ServeWatchRole.FromOutcome(outcome));
    }

    private static string? ResolveEmbedDrainReason(bool embedWatch, bool? embedPendingInConfig) =>
        embedWatch switch
        {
            false => "--no-embed-watch",
            _ when embedPendingInConfig == false => "watch.embedPending=false",
            _ => null
        };

    // Runs on a worker thread off the JSON-RPC startup path: provider resolution may probe an HTTP
    // endpoint before the runtime is usable. Whatever happens here, serve must keep answering.
    private static WatchEmbeddingRuntime? CreateWatchEmbeddingRuntime(string workspaceRoot, TextWriter stderr)
    {
        try
        {
            var warnings = EmbeddingRuntimeFactory.TryCreate(
                workspaceRoot, withProvider: true, out var provider, out var store, out var resolution);
            if (provider is null || store is null)
            {
                stderr.WriteLine(
                    "warning: watch embed drain off: " +
                    (warnings.Count > 0 ? warnings[0] : "embedding runtime unavailable"));
                return null;
            }

            var providerName = resolution?.Provider ?? "unknown";
            stderr.WriteLine($"squeezr serve: watch embed drain ready (provider={providerName})");
            return new WatchEmbeddingRuntime(provider, store, resolution);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            stderr.WriteLine($"warning: watch embed drain off: {ex.Message}");
            return null;
        }
    }

    private static string VersionString() => AppVersion.Current;
}
