using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Shouldly;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Daemons;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class FleetManagementTests : IAsyncLifetime
{
    private readonly List<Process> _daemons = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var daemon in _daemons)
        {
            TryKill(daemon);
            daemon.Dispose();
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Stop_BothKindsAlive_StopsGracefully_ProcessesExit_HeartbeatsDead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-stop");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var storeDir = SqueezrPaths.StoreDir(root);
        var watchDaemon = StartFakeSqueezrDaemon();
        var serveWatchDaemon = StartFakeSqueezrDaemon();
        WriteHeartbeat(storeDir, DaemonHeartbeat.DefaultKind, watchDaemon.Id, DateTime.UtcNow, DateTime.UtcNow);
        WriteHeartbeat(storeDir, DaemonHeartbeat.ServeWatchKind, serveWatchDaemon.Id, DateTime.UtcNow, DateTime.UtcNow);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "stop", storeKey, "--json");

        result.ExitCode.ShouldBe(0);
        result.StdErr.ShouldBe(string.Empty);
        var payload = SingleJsonLine(result.StdOut);
        payload.GetProperty("storeKey").GetString().ShouldBe(storeKey);
        var results = payload.GetProperty("results");
        results.GetArrayLength().ShouldBe(2);
        var watch = results[0];
        watch.GetProperty("kind").GetString().ShouldBe(DaemonHeartbeat.DefaultKind);
        watch.GetProperty("status").GetString().ShouldBe("stopped");
        watch.GetProperty("detail").GetString().ShouldBe("graceful");
        watch.GetProperty("pid").GetInt32().ShouldBe(watchDaemon.Id);
        var serveWatch = results[1];
        serveWatch.GetProperty("kind").GetString().ShouldBe(DaemonHeartbeat.ServeWatchKind);
        serveWatch.GetProperty("status").GetString().ShouldBe("stopped");
        serveWatch.GetProperty("detail").GetString().ShouldBe("graceful");
        serveWatch.GetProperty("pid").GetInt32().ShouldBe(serveWatchDaemon.Id);

        (await WaitForExitAsync(watchDaemon, TimeSpan.FromSeconds(10))).ShouldBeTrue();
        (await WaitForExitAsync(serveWatchDaemon, TimeSpan.FromSeconds(10))).ShouldBeTrue();
        HeartbeatAlive(storeDir, DaemonHeartbeat.DefaultKind).ShouldBeFalse();
        HeartbeatAlive(storeDir, DaemonHeartbeat.ServeWatchKind).ShouldBeFalse();
    }

    [Fact]
    public async Task Stop_StaleHeartbeat_NoRunningDaemons_Exit0WithWarning()
    {
        var name = UniqueName("fleet-stop-idle");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var staleBeat = DateTime.UtcNow.AddHours(-1);
        WriteHeartbeat(SqueezrPaths.StoreDir(root), DaemonHeartbeat.DefaultKind, Environment.ProcessId, staleBeat, staleBeat);

        var json = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "stop", storeKey, "--json");
        json.ExitCode.ShouldBe(0);
        var results = SingleJsonLine(json.StdOut).GetProperty("results");
        results.GetArrayLength().ShouldBe(2);
        results[0].GetProperty("status").GetString().ShouldBe("not-running");
        results[1].GetProperty("status").GetString().ShouldBe("not-running");

        var human = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "stop", storeKey);
        human.ExitCode.ShouldBe(0);
        human.StdErr.ShouldContain("no running daemons");
    }

    [Fact]
    public async Task Stop_ForeignPid_RefusesWithError_LeavesProcessAlive()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-stop-foreign");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var foreign = StartProcess("/bin/sleep", "300");
        WriteHeartbeat(SqueezrPaths.StoreDir(root), DaemonHeartbeat.DefaultKind, foreign.Id, DateTime.UtcNow, DateTime.UtcNow);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "stop", storeKey, "--json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain($"pid {foreign.Id} is not a squeezr process");
        foreign.HasExited.ShouldBeFalse();
    }

    [Fact]
    public async Task Stop_UnknownStoreKey_ErrorsWithoutResolution()
    {
        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "stop", "no-such-key", "--json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("No workspace is registered with store key");
        result.StdErr.ShouldContain("no-such-key");
    }

    [Fact]
    public async Task Restart_StaleRootPath_ErrorsWithDropHint()
    {
        var name = UniqueName("fleet-restart-stale");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        Directory.Delete(root, recursive: true);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "restart", storeKey, "--json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("root path");
        result.StdErr.ShouldContain("squeezr drop --store-key");
    }

    [Fact]
    public async Task Restart_OnlyServeWatchAlive_ErrorsWithMcpHint_LeavesDaemonRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-restart-serve");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var serveWatchDaemon = StartFakeSqueezrDaemon();
        WriteHeartbeat(
            SqueezrPaths.StoreDir(root),
            DaemonHeartbeat.ServeWatchKind,
            serveWatchDaemon.Id,
            DateTime.UtcNow,
            DateTime.UtcNow);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "restart", storeKey, "--json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("squeezr mcp install");
        serveWatchDaemon.HasExited.ShouldBeFalse();
    }

    [Fact]
    public async Task Restart_NoWatchRunning_SpawnsDaemonWithWorkspaceCwd()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-restart-spawn");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var fake = CreateFakeSqueezrBinary();

        var result = await RunInProcessAsync(
            (typeof(FleetRestartCommand), new FleetRestartCommand(new DaemonLauncher(() => fake))),
            "restart", storeKey, "--json");

        result.ExitCode.ShouldBe(0);
        result.StdErr.ShouldBe(string.Empty);
        var payload = SingleJsonLine(result.StdOut);
        payload.GetProperty("previousStatus").GetString().ShouldBe("not-running");
        payload.GetProperty("previousPid").ValueKind.ShouldBe(JsonValueKind.Null);
        var pid = payload.GetProperty("pid").GetInt32();
        pid.ShouldBeGreaterThan(0);
        payload.GetProperty("watcherAlive").GetBoolean().ShouldBeTrue();
        (await MarkerFor(fake)).ShouldBe(Path.GetFullPath(root) + "|watch --await-claim");
        TrackSpawned(pid).HasExited.ShouldBeFalse();
    }

    [Fact]
    public async Task Restart_UnderDotnetMuxerd_SpawnsAssemblyWithWatchArgument()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-restart-muxer");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var muxerd = CreateFakeMuxerdBinary();

        var result = await RunInProcessAsync(
            (typeof(FleetRestartCommand), new FleetRestartCommand(new DaemonLauncher(() => muxerd))),
            "restart", storeKey, "--json");

        result.ExitCode.ShouldBe(0);
        var payload = SingleJsonLine(result.StdOut);
        var pid = payload.GetProperty("pid").GetInt32();
        var expectedAssemblyPath = Assembly.GetEntryAssembly()!.Location;
        (await MarkerFor(muxerd)).ShouldBe(Path.GetFullPath(root) + "|" + expectedAssemblyPath + " watch --await-claim");
        TrackSpawned(pid).HasExited.ShouldBeFalse();
    }

    [Fact]
    public async Task Stop_HeartbeatStartedAtInFuture_RefusesAsReusedPid()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-stop-reuse");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var daemon = StartFakeSqueezrDaemon();
        var futureStartedAt = DateTime.UtcNow.AddMinutes(5);
        WriteHeartbeat(SqueezrPaths.StoreDir(root), DaemonHeartbeat.DefaultKind, daemon.Id, futureStartedAt, DateTime.UtcNow);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "stop", storeKey, "--json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain($"pid {daemon.Id} was reused");
        daemon.HasExited.ShouldBeFalse();
    }

    [Fact]
    public async Task Restart_AliveWatch_StopsOldDaemonAndSpawnsNewOne()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-restart-live");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var oldDaemon = StartFakeSqueezrDaemon();
        WriteHeartbeat(SqueezrPaths.StoreDir(root), DaemonHeartbeat.DefaultKind, oldDaemon.Id, DateTime.UtcNow, DateTime.UtcNow);
        var fake = CreateFakeSqueezrBinary();

        var result = await RunInProcessAsync(
            (typeof(FleetRestartCommand), new FleetRestartCommand(new DaemonLauncher(() => fake))),
            "restart", storeKey, "--json");

        result.ExitCode.ShouldBe(0);
        var payload = SingleJsonLine(result.StdOut);
        payload.GetProperty("previousStatus").GetString().ShouldBe("graceful");
        payload.GetProperty("previousPid").GetInt32().ShouldBe(oldDaemon.Id);
        var newPid = payload.GetProperty("pid").GetInt32();
        newPid.ShouldNotBe(oldDaemon.Id);
        (await WaitForExitAsync(oldDaemon, TimeSpan.FromSeconds(10))).ShouldBeTrue();
        (await MarkerFor(fake)).ShouldBe(Path.GetFullPath(root) + "|watch --await-claim");
        TrackSpawned(newPid).HasExited.ShouldBeFalse();
    }

    // R2a: the restart that follows a killed owner must launch the way the supervisor does. A bare
    // 'watch' reads the dead owner's claim, declines it with OwnerHold and returns 0 — the reported
    // pid would be a corpse; --await-claim waits the grace out and takes the store over.
    [Fact]
    public async Task Restart_ClaimHeldByDeadOwner_LaunchesSupervisedAndReportsALivePid()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-restart-deadowner");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        WriteClaim(SqueezrPaths.StoreDir(root), new WatchOwnerMarker(
            int.MaxValue, Environment.MachineName, DaemonHeartbeat.DefaultKind,
            DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow));
        var fake = CreateFakeSqueezrBinary();

        var result = await RunInProcessAsync(
            (typeof(FleetRestartCommand), new FleetRestartCommand(new DaemonLauncher(() => fake))),
            "restart", storeKey, "--json");

        result.ExitCode.ShouldBe(0, $"out: {result.StdOut} err: {result.StdErr}");
        var payload = SingleJsonLine(result.StdOut);
        payload.GetProperty("previousStatus").GetString().ShouldBe("not-running");
        payload.GetProperty("watcherAlive").GetBoolean().ShouldBeTrue();
        (await MarkerFor(fake)).ShouldBe(Path.GetFullPath(root) + "|watch --await-claim");
        TrackSpawned(payload.GetProperty("pid").GetInt32()).HasExited.ShouldBeFalse();
    }

    [Fact]
    public async Task Restart_DaemonDiesBeforeItSettles_ReportsPending_NotASuccessfulRestart()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-restart-pending");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var fake = CreateFakeSqueezrBinaryThatExits();
        var command = new FleetRestartCommand(new DaemonLauncher(() => fake));

        var result = await RunInProcessAsync((typeof(FleetRestartCommand), command), "restart", storeKey, "--json");

        result.ExitCode.ShouldBe(0, $"out: {result.StdOut} err: {result.StdErr}");
        var payload = SingleJsonLine(result.StdOut);
        payload.GetProperty("watcherAlive").GetBoolean().ShouldBeFalse(
            "a pid that was already gone at report time is not a restarted watcher");
        payload.GetProperty("desired").GetString().ShouldBe("running");
    }

    // The in-process harness cannot see AnsiConsole output, so the human verdict is pinned at the seam
    // that renders it: the same line fleet restart writes when the spawn did not survive.
    [Fact]
    public void ReportVerdict_DaemonGoneBeforeItSettled_NamesThePendingVerdictInsteadOfSuccess()
    {
        var console = new TestConsole();

        FleetRestartCommand.ReportVerdict(
            new FleetRestartResult("demo", "demo-key", "graceful", 4242, 9999, "running", WatcherAlive: false),
            console);

        console.Output.ShouldContain("restart pending");
        console.Output.ShouldContain("supervisor will converge");
        console.Output.ShouldNotContain("restarted");
        console.Output.ShouldNotContain("started (pid");
    }

    [Fact]
    public void ReportVerdict_LiveDaemon_StillNamesTheNewPid()
    {
        var console = new TestConsole();

        FleetRestartCommand.ReportVerdict(
            new FleetRestartResult("demo", "demo-key", "graceful", 4242, 9999),
            console);

        console.Output.ShouldContain("restarted (pid 4242 stopped graceful, new pid 9999)");
    }

    [Fact]
    public async Task Stop_ClaimNamesOneDaemon_MirrorNamesAnother_KillsOnlyTheClaimed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-stop-claim");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var storeDir = SqueezrPaths.StoreDir(root);
        var ownerDaemon = StartFakeSqueezrDaemon();
        var observerDaemon = StartFakeSqueezrDaemon();
        WriteClaim(storeDir, new WatchOwnerMarker(
            ownerDaemon.Id, Environment.MachineName, DaemonHeartbeat.DefaultKind,
            ownerDaemon.StartTime.ToUniversalTime(), DateTime.UtcNow));
        WriteHeartbeat(storeDir, DaemonHeartbeat.DefaultKind, ownerDaemon.Id,
            ownerDaemon.StartTime.ToUniversalTime(), DateTime.UtcNow);
        WriteHeartbeat(storeDir, DaemonHeartbeat.ServeWatchKind, observerDaemon.Id,
            observerDaemon.StartTime.ToUniversalTime(), DateTime.UtcNow);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "stop", storeKey, "--json");

        result.ExitCode.ShouldBe(0);
        var payload = SingleJsonLine(result.StdOut);
        var results = payload.GetProperty("results");
        results[0].GetProperty("kind").GetString().ShouldBe(DaemonHeartbeat.DefaultKind);
        results[0].GetProperty("pid").GetInt32().ShouldBe(ownerDaemon.Id,
            "fleet stop killed the mirror's pid instead of the one the claim names");
        results[1].GetProperty("kind").GetString().ShouldBe(DaemonHeartbeat.ServeWatchKind);
        results[1].GetProperty("status").GetString().ShouldBe("not-running");
        payload.GetProperty("observers").GetInt32().ShouldBe(1);

        (await WaitForExitAsync(ownerDaemon, TimeSpan.FromSeconds(10))).ShouldBeTrue();
        observerDaemon.HasExited.ShouldBeFalse(
            "fleet stop killed an observer by surprise: it is some MCP client's live server");
    }

    [Fact]
    public async Task Stop_ClaimOwnerGone_KillsNothing_ObserversSurviveAndAreNamed()
    {
        var name = UniqueName("fleet-stop-gone");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var storeDir = SqueezrPaths.StoreDir(root);
        var observerDaemon = StartFakeSqueezrDaemon();
        WriteClaim(storeDir, new WatchOwnerMarker(
            int.MaxValue, Environment.MachineName, DaemonHeartbeat.DefaultKind,
            DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow));
        WriteHeartbeat(storeDir, DaemonHeartbeat.ServeWatchKind, observerDaemon.Id,
            observerDaemon.StartTime.ToUniversalTime(), DateTime.UtcNow);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "stop", storeKey, "--json");

        result.ExitCode.ShouldBe(0);
        var payload = SingleJsonLine(result.StdOut);
        payload.GetProperty("results")[0].GetProperty("status").GetString().ShouldBe("not-running");
        payload.GetProperty("observers").GetInt32().ShouldBe(1);
        observerDaemon.HasExited.ShouldBeFalse("a gone owner's pid made fleet stop kill a live observer");

        var human = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "stop", storeKey);
        human.StdErr.ShouldContain("names pid");
    }

    [Fact]
    public async Task Restart_ClaimHeldByServeWatchDaemon_ErrorsWithMcpHint_LeavesOwnerRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-restart-claim");
        var root = await NewInitializedWorkspaceAsync(name);
        var storeKey = StoreKeyFor(root);
        var ownerDaemon = StartFakeSqueezrDaemon();
        WriteClaim(SqueezrPaths.StoreDir(root), new WatchOwnerMarker(
            ownerDaemon.Id, Environment.MachineName, DaemonHeartbeat.ServeWatchKind,
            ownerDaemon.StartTime.ToUniversalTime(), DateTime.UtcNow));

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "restart", storeKey, "--json");

        result.ExitCode.ShouldBe(1, "fleet restart restarted a store whose watch loop belongs to an MCP client");
        result.StdErr.ShouldContain("owned by the MCP client");
        result.StdErr.ShouldContain($"pid {ownerDaemon.Id}");
        ownerDaemon.HasExited.ShouldBeFalse();
    }

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static async Task<string> NewInitializedWorkspaceAsync(string name)
    {
        var root = TestDirs.NewTempDir();
        var init = await RunCliAsync(root, "init", "--name", name, "--json");
        init.ExitCode.ShouldBe(0);
        return root;
    }

    private static string StoreKeyFor(string root) =>
        WorkspaceRegistry.FindByRootPath(root)!.StoreKey;

    private static void WriteHeartbeat(string storeDir, string kind, int pid, DateTime startedAt, DateTime lastBeatAt)
    {
        var json = JsonSerializer.Serialize(new DaemonHeartbeat(pid, kind, startedAt, lastBeatAt), SqueezrJson.FileOptions);
        File.WriteAllText(DaemonHeartbeat.PathFor(storeDir, kind), json);
    }

    private static void WriteClaim(string storeDir, WatchOwnerMarker marker) =>
        File.WriteAllText(
            WatchOwnerMarker.PathFor(storeDir), JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));

    private static bool HeartbeatAlive(string storeDir, string kind)
    {
        var heartbeat = DaemonHeartbeatReader.TryRead(storeDir, kind);
        return heartbeat is not null && DaemonHeartbeatReader.IsAlive(heartbeat, DateTime.UtcNow);
    }

    private Process StartFakeSqueezrDaemon()
    {
        var fake = CreateFakeSqueezrBinary();
        return StartProcess("/bin/sh", fake, "daemon");
    }

    private static string CreateFakeSqueezrBinary() => CreateFakeExecutable("squeezr");

    private static string CreateFakeSqueezrBinaryThatExits() => CreateFakeExecutable("squeezr", staysUp: false);

    private static string CreateFakeMuxerdBinary() => CreateFakeExecutable("dotnet");

    private static string CreateFakeExecutable(string fileName, bool staysUp = true)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("The fake squeezr binary requires a Unix host.");
        }

        var dir = TestDirs.NewTempDir();
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(
            path,
            "#!/bin/sh\n" +
            "echo \"$PWD|$@\" > \"$(dirname \"$0\")/marker\"\n" +
            (staysUp ? "while :; do sleep 1; done\n" : "exit 0\n"));
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return path;
    }

    private static async Task<string> MarkerFor(string fakeBinaryPath)
    {
        var markerPath = Path.Combine(Path.GetDirectoryName(fakeBinaryPath)!, "marker");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(markerPath))
        {
            await Task.Delay(50, cts.Token);
        }

        return (await File.ReadAllTextAsync(markerPath)).TrimEnd('\n');
    }

    private Process StartProcess(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)!;
        _daemons.Add(process);
        return process;
    }

    private Process TrackSpawned(int pid)
    {
        var process = Process.GetProcessById(pid);
        _daemons.Add(process);
        return process;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static JsonElement SingleJsonLine(string stdout)
    {
        stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1);
        using var doc = JsonDocument.Parse(stdout.Trim());
        return doc.RootElement.Clone();
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunInProcessAsync(
        (Type Service, object Instance)? injection, params string[] args)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            CommandApp app = injection is { } registered
                ? new CommandApp(new SingleInstanceRegistrar(registered.Service, registered.Instance))
                : new CommandApp();
            app.Configure(config =>
            {
                config.AddCommand<FleetStopCommand>("stop");
                config.AddCommand<FleetRestartCommand>("restart");
            });
            var code = await app.RunAsync(args);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    private sealed class SingleInstanceRegistrar : ITypeRegistrar
    {
        private readonly Dictionary<Type, object> _instances;

        public SingleInstanceRegistrar(Type service, object instance) => _instances = new() { [service] = instance };

        public void Register(Type service, Type implementation)
        {
        }

        public void RegisterInstance(Type service, object implementation) => _instances[service] = implementation;

        public void RegisterLazy(Type service, Func<object> factory) => _instances[service] = factory();

        public ITypeResolver Build() => new Resolver(_instances);

        private sealed class Resolver(IReadOnlyDictionary<Type, object> instances) : ITypeResolver
        {
            public object? Resolve(Type? type)
            {
                if (type is null)
                {
                    return null;
                }

                if (instances.TryGetValue(type, out var instance))
                {
                    return instance;
                }

                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return Array.CreateInstance(type.GetGenericArguments()[0], 0);
                }

                if (type.IsInterface || type.IsAbstract)
                {
                    return null;
                }

                try
                {
                    return Activator.CreateInstance(type);
                }
                catch (MissingMethodException)
                {
                    return null;
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
