using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Mcp.Onboarding;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Mcp.Onboarding;

// The Render cases swap the process-wide AnsiConsole, so the whole class rides the serialized collection.
[Collection("ProcessGlobals")]
public sealed class DoctorStoreWatchTests
{
    [Fact]
    public async Task Doctor_OnUnregisteredWorkspace_CarriesNoWatchRow()
    {
        var (service, environment) = NewFixture();

        var diagnostics = await service.DoctorAsync(Client(), environment, CancellationToken.None);

        diagnostics.Watch.ShouldBeNull("a root nothing registered has no store to name an indexer of");
    }

    [Fact]
    public async Task Doctor_OnUnwatchedStore_NamesNone_HandsTheExactStartCommand_AndSerializesAdditively()
    {
        var (service, environment, workspace) = NewRegisteredWorkspace();

        var diagnostics = await service.DoctorAsync(Client(), environment, CancellationToken.None);

        var watch = diagnostics.Watch!;
        watch.OwnerState.ShouldBe("none");
        watch.OwnerPid.ShouldBeNull();
        watch.Observers.ShouldBe(0);
        var entry = WorkspaceRegistry.FindByRootPath(workspace)!;
        watch.StoreKey.ShouldBe(entry.StoreKey);
        watch.Name.ShouldBe(entry.Name);
        watch.StartCommand.ShouldBe($"squeezr fleet restart {entry.StoreKey}");

        var serialized = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        using var document = JsonDocument.Parse(serialized);
        var root = document.RootElement;
        // the pre-existing keys answer the same values they always did — the watch object is purely extra
        root.TryGetProperty("client", out _).ShouldBeTrue();
        root.TryGetProperty("userScopeEntry", out _).ShouldBeTrue();
        var watchJson = root.GetProperty("watch");
        watchJson.GetProperty("ownerState").GetString().ShouldBe("none");
        watchJson.GetProperty("startCommand").GetString().ShouldBe($"squeezr fleet restart {entry.StoreKey}");
    }

    [Fact]
    public async Task Doctor_NamesTheLiveOwner_AndCountsObservers()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (service, environment, workspace) = NewRegisteredWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind).TryAcquire().IsOwner
            .ShouldBeTrue("test failed to hold the claim the scenario arranges");
        using var attached = Process.Start("sleep", "30")!;
        PlantBeat(storeDir, new DaemonHeartbeat(
            attached.Id, DaemonHeartbeat.DefaultKind, DateTime.UtcNow, DateTime.UtcNow, Environment.MachineName));

        try
        {
            var diagnostics = await service.DoctorAsync(Client(), environment, CancellationToken.None);

            var watch = diagnostics.Watch!;
            watch.OwnerState.ShouldBe("alive");
            watch.OwnerPid.ShouldBe(Environment.ProcessId);
            watch.OwnerKind.ShouldBe(DaemonHeartbeat.ServeWatchKind);
            watch.Observers.ShouldBe(1);
            watch.StartCommand.ShouldBeNull("a store with a running indexer has nothing to start");
        }
        finally
        {
            new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind).Release();
            TryKill(attached);
        }
    }

    [Fact]
    public void Render_NamesTheOwner_AndCountsObservers_InText()
    {
        var diagnostics = new[]
        {
            NewDiagnostics(new StoreWatchDiagnostics(
                "demo", "demo-abc12345", "alive", 4242, DaemonHeartbeat.ServeWatchKind, Environment.MachineName, 2, null)),
        };
        var previous = AnsiConsole.Console;
        var console = new TestConsole();
        AnsiConsole.Console = console;
        try
        {
            McpDoctorCommand.Render(diagnostics);
        }
        finally
        {
            AnsiConsole.Console = previous;
        }

        console.Output.ShouldContain("watch 'demo': owner pid 4242 (serve-watch) — 2 observer(s)");
    }

    [Fact]
    public void Render_UnwatchedStore_PrintsTheExactStartCommand()
    {
        var diagnostics = new[]
        {
            NewDiagnostics(new StoreWatchDiagnostics(
                "demo", "demo-abc12345", "none", null, null, null, 0, "squeezr fleet restart demo-abc12345")),
        };
        var previous = AnsiConsole.Console;
        var console = new TestConsole();
        AnsiConsole.Console = console;
        try
        {
            McpDoctorCommand.Render(diagnostics);
        }
        finally
        {
            AnsiConsole.Console = previous;
        }

        console.Output.ShouldContain("no background indexer");
        console.Output.ShouldContain("start: squeezr fleet restart demo-abc12345");
    }

    private static McpClientDiagnostics NewDiagnostics(StoreWatchDiagnostics watch) =>
        new("kimi", true, false, true, "cli", "writer", true, true, null, [],
            null, null, false, false, null, null, watch);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
        }
    }

    private static McpClientDescriptor Client() => McpClientRegistry.Get(McpClientId.Kimi);

    private static (McpOnboardingService, McpOnboardingEnvironment) NewFixture()
    {
        var home = TestDirs.NewTempDir();
        var workspace = TestDirs.NewTempDir();
        var environment = new McpOnboardingEnvironment(home, workspace, Environment.ProcessPath!, null);
        return (new McpOnboardingService(new ClientCliLocator(string.Empty)), environment);
    }

    private static (McpOnboardingService, McpOnboardingEnvironment, string) NewRegisteredWorkspace()
    {
        var (service, environment) = NewFixture();
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("doctor-watch-demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        SemanticConfigFile.Save(workspace, SemanticConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { Enabled = false, DebounceSeconds = 1 }
        });
        File.WriteAllText(Path.Combine(workspace, "repo", "a.cs"), "class Alpha { string Term = \"uniqneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var environmentOnWorkspace = new McpOnboardingEnvironment(
            environment.HomeDir, workspace, environment.CommandPath, environment.SessionDirectory);
        return (service, environmentOnWorkspace, workspace);
    }

    private static void PlantBeat(string storeDir, DaemonHeartbeat beat)
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(storeDir));
        File.WriteAllText(
            DaemonHeartbeat.BeatPathFor(storeDir, beat.Kind, beat.Pid),
            JsonSerializer.Serialize(beat, SqueezrJson.FileOptions));
    }
}
