using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Spectre.Console.Cli;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Daemons;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class FleetDesiredStateTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<Process> _daemons = [];

    public FleetDesiredStateTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable("SQUEEZR_HOME");
        _home = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable("SQUEEZR_HOME", _home);
        SqueezrHome.Reset();
    }

    public void Dispose()
    {
        foreach (var daemon in _daemons)
        {
            TryKill(daemon);
            daemon.Dispose();
        }

        Environment.SetEnvironmentVariable("SQUEEZR_HOME", _previousEnv);
        SqueezrHome.Reset();
    }

    [Fact]
    public async Task Start_AfterStop_RestoresRunningDesire_AndNamesTheAbsentWatchd()
    {
        var (root, storeKey) = await NewWorkspaceAsync("fleet-start");

        var stopped = await RunInProcessAsync(null, "stop", storeKey, "--json");
        stopped.ExitCode.ShouldBe(0);
        NewState().DesireOf(storeKey).ShouldBe(WatchDesire.Stopped);

        var started = await RunInProcessAsync(null, "start", storeKey, "--json");

        started.ExitCode.ShouldBe(0);
        started.StdErr.ShouldBe(string.Empty);
        var payload = SingleJsonLine(started.StdOut);
        payload.GetProperty("desired").GetString().ShouldBe("running");
        payload.GetProperty("watchd").GetProperty("running").GetBoolean().ShouldBeFalse();
        NewState().DesireOf(storeKey).ShouldBe(WatchDesire.Running);
    }

    [Fact]
    public async Task Start_UnknownStoreKey_ErrorsWithoutTouchingState()
    {
        var result = await RunInProcessAsync(null, "start", "no-such-key", "--json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("No workspace is registered with store key");
    }

    [Fact]
    public async Task Stop_WithNothingAlive_StillRecordsTheStoppedDesire()
    {
        var (root, storeKey) = await NewWorkspaceAsync("fleet-stop-desire");

        var result = await RunInProcessAsync(null, "stop", storeKey, "--json");

        result.ExitCode.ShouldBe(0);
        var payload = SingleJsonLine(result.StdOut);
        payload.GetProperty("desired").GetString().ShouldBe("stopped");
        payload.GetProperty("results").GetArrayLength().ShouldBe(2);
        NewState().DesireOf(storeKey).ShouldBe(WatchDesire.Stopped);
    }

    [Fact]
    public async Task Stop_KillsLiveOwner_RecordsStopped_SupervisorKeepsItDown()
    {
        var (root, storeKey) = await NewWorkspaceAsync("fleet-stop-kill");
        var storeDir = SqueezrPaths.StoreDir(root);
        var daemon = StartFakeSqueezrDaemon();
        WriteHeartbeat(storeDir, DaemonHeartbeat.DefaultKind, daemon.Id, DateTime.UtcNow, DateTime.UtcNow);

        var result = await RunInProcessAsync(null, "stop", storeKey, "--json");

        result.ExitCode.ShouldBe(0);
        var payload = SingleJsonLine(result.StdOut);
        payload.GetProperty("results")[0].GetProperty("status").GetString().ShouldBe("stopped");
        payload.GetProperty("desired").GetString().ShouldBe("stopped");
        (await WaitForExitAsync(daemon, TimeSpan.FromSeconds(10))).ShouldBeTrue();
        NewState().DesireOf(storeKey).ShouldBe(WatchDesire.Stopped);

        var launcher = new RecordingLauncher();
        var loop = NewLoop(launcher, WorkspaceRegistry.FindByRootPath(root)!);
        loop.RunOnce().Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.LeftStopped);
        launcher.Roots.ShouldBeEmpty("a stopped store is an intention the supervisor must keep");
    }

    [Fact]
    public async Task Restart_ClearsWedgedGuard_SetsRunningDesire_SpawnsFreshDaemon()
    {
        var (root, storeKey) = await NewWorkspaceAsync("fleet-restart-wedge");
        var guard = new CrashLoopGuard();
        var stamp = DateTime.UtcNow;
        while (!guard.IsWedged(storeKey))
        {
            guard.RegisterRestart(storeKey, stamp);
        }

        var fake = CreateFakeSqueezrBinary();
        var command = new FleetRestartCommand(
            new DaemonLauncher(() => fake), NewState(), guard);

        var result = await RunInProcessAsync(
            (typeof(FleetRestartCommand), command), "restart", storeKey, "--json");

        result.ExitCode.ShouldBe(0, $"out: {result.StdOut} err: {result.StdErr}");
        var payload = SingleJsonLine(result.StdOut);
        payload.GetProperty("previousStatus").GetString().ShouldBe("not-running");
        payload.GetProperty("desired").GetString().ShouldBe("running");
        var pid = payload.GetProperty("pid").GetInt32();
        pid.ShouldBeGreaterThan(0);
        payload.GetProperty("watcherAlive").GetBoolean().ShouldBeTrue();
        guard.IsWedged(storeKey).ShouldBeFalse("fleet restart is the documented way out of a crash-loop wedge");
        NewState().DesireOf(storeKey).ShouldBe(WatchDesire.Running);
        TrackSpawned(pid).HasExited.ShouldBeFalse();
    }

    [Fact]
    public async Task Status_ReflectsDesiredState_AndTheAbsentWatchd()
    {
        var (root, storeKey) = await NewWorkspaceAsync("fleet-status");
        (await RunInProcessAsync(null, "stop", storeKey, "--json")).ExitCode.ShouldBe(0);

        var json = await RunInProcessAsync(null, "status", "--json");
        var human = await RunInProcessAsync(null, "status");

        json.ExitCode.ShouldBe(0, $"out: {json.StdOut} err: {json.StdErr}");
        var payload = SingleJsonLine(json.StdOut);
        payload.GetProperty("watchd").GetProperty("running").GetBoolean().ShouldBeFalse();
        var store = payload.GetProperty("stores")[0];
        store.GetProperty("name").GetString().ShouldNotBeEmpty();
        store.GetProperty("desired").GetString().ShouldBe("stopped");
        store.GetProperty("supervision").GetString().ShouldBe("stopped");
        store.GetProperty("indexerState").GetString().ShouldBe("none");
        store.GetProperty("ownerPid").ValueKind.ShouldBe(JsonValueKind.Null);
        store.TryGetProperty("lastPass", out _).ShouldBeFalse(
            "a clean last pass leaves no record and the JSON omits it");
        human.ExitCode.ShouldBe(0);
        human.StdErr.ShouldContain("watchd is not running");
    }

    [Fact]
    public async Task Status_NothingAliveNoWatchd_WantedStoreReadsIdleStoppedStoreReadsStopped()
    {
        var (_, runningKey) = await NewWorkspaceAsync("fleet-idle-row");
        var (_, stoppedKey) = await NewWorkspaceAsync("fleet-stopped-row");
        NewState().SetDesire(stoppedKey, WatchDesire.Stopped);

        var json = await RunInProcessAsync(null, "status", "--json");
        var report = SingleJsonLine(json.StdOut);
        report.TryGetProperty("stateFault", out _).ShouldBeFalse(
            "a state file that reads clean adds nothing to the shape");
        Row(report, runningKey).GetProperty("supervision").GetString().ShouldBe("idle (no supervisor)");
        Row(report, stoppedKey).GetProperty("supervision").GetString().ShouldBe("stopped");
        Row(report, runningKey).GetProperty("desired").GetString().ShouldBe("running");
        Row(report, stoppedKey).GetProperty("desired").GetString().ShouldBe("stopped");

        var human = await CliTestProcess.RunWithHomeAsync(_home, TestDirs.NewTempDir(), "fleet", "status");
        human.ExitCode.ShouldBe(0, $"out: {human.StdOut} err: {human.StdErr}");
        human.StdOut.ShouldContain("idle (no supervisor)");
    }

    [Fact]
    public async Task Status_StateFileUnreadable_NamesTheFault_AndNeverClaimsARunningWish()
    {
        var (_, storeKey) = await NewWorkspaceAsync("fleet-corrupt-state");
        WriteRawState("{ \"stores\": oops");

        var json = await RunInProcessAsync(null, "status", "--json");
        json.ExitCode.ShouldBe(0, $"out: {json.StdOut} err: {json.StdErr}");
        var report = SingleJsonLine(json.StdOut);
        var row = Row(report, storeKey);
        row.GetProperty("desired").GetString().ShouldBe("unknown");
        row.GetProperty("supervision").GetString().ShouldBe("idle (no supervisor)");
        report.GetProperty("stateFault").GetString().ShouldContain("watchd");
        report.GetProperty("stateFault").GetString().ShouldContain("not valid JSON");

        var human = await CliTestProcess.RunWithHomeAsync(_home, TestDirs.NewTempDir(), "fleet", "status");
        human.StdOut.ShouldContain("unknown");
        human.StdErr.ShouldContain("no desired state is known");
    }

    [Fact]
    public async Task Stop_StaleRoot_Refuses_AndWritesNoDesire()
    {
        var (root, storeKey) = await NewWorkspaceAsync("fleet-stop-stale");
        Directory.Delete(root, recursive: true);

        var result = await RunInProcessAsync(null, "stop", storeKey, "--json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("Cannot stop workspace");
        result.StdErr.ShouldContain("root path");
        result.StdErr.ShouldContain($"squeezr drop --store-key {storeKey}");
        NewState().Read().Desires.ContainsKey(storeKey).ShouldBeFalse(
            "a refusal must not leave a wish for a store this machine cannot reach");
    }

    [Fact]
    public async Task Start_StaleRoot_Refuses_AndWritesNoDesire()
    {
        var (root, storeKey) = await NewWorkspaceAsync("fleet-start-stale");
        Directory.Delete(root, recursive: true);

        var result = await RunInProcessAsync(null, "start", storeKey, "--json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("Cannot start workspace");
        result.StdErr.ShouldContain($"squeezr drop --store-key {storeKey}");
        NewState().Read().Desires.ContainsKey(storeKey).ShouldBeFalse();
    }

    [Fact]
    public async Task ServeNoticeReading_WatchdHoldingTheLockOverACorruptState_AnswersUnreadable()
    {
        var (root, _) = await NewWorkspaceAsync("fleet-notice-reading-corrupt");
        using var watchd = HoldWatchdLock();
        WriteRawState("{ \"stores\": oops");

        NoIndexerNotice.ReadSupervision(root).ShouldBe(NoIndexerNotice.Supervision.StateUnreadable);
    }

    [Fact]
    public async Task ServeNoticeReading_LiveWatchdOverAWantedStore_AnswersItWillBringItUp()
    {
        var (root, _) = await NewWorkspaceAsync("fleet-notice-reading-running");
        using var watchd = HoldWatchdLock();

        NoIndexerNotice.ReadSupervision(root).ShouldBe(NoIndexerNotice.Supervision.WillBringItUp);
    }

    [Fact]
    public async Task ServeNoticeReading_LiveWatchdOverAStoppedStore_AnswersNone()
    {
        var (root, storeKey) = await NewWorkspaceAsync("fleet-notice-reading-stopped");
        using var watchd = HoldWatchdLock();
        NewState().SetDesire(storeKey, WatchDesire.Stopped);

        NoIndexerNotice.ReadSupervision(root).ShouldBe(NoIndexerNotice.Supervision.None);
    }

    [Fact]
    public async Task ServeNoticeReading_NoWatchdAlive_AnswersNone_EvenOverACorruptState()
    {
        var (root, _) = await NewWorkspaceAsync("fleet-notice-reading-dead");
        WriteRawState("{ \"stores\": oops");

        NoIndexerNotice.ReadSupervision(root).ShouldBe(NoIndexerNotice.Supervision.None);
    }

    [Fact]
    public async Task FleetListJson_KeepsItsShape_AlongsideTheDesiredStateWork()
    {
        var (root, storeKey) = await NewWorkspaceAsync("fleet-compat");

        var result = await RunInProcessAsync(null, "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        var entry = JsonLines(result.StdOut)
            .Single(line => line.GetProperty("storeKey").GetString() == storeKey);
        entry.GetProperty("name").GetString().ShouldNotBeEmpty();
        entry.GetProperty("watch").GetProperty("status").GetString().ShouldNotBeEmpty();
        entry.GetProperty("serveWatch").GetProperty("status").GetString().ShouldNotBeEmpty();
        entry.GetProperty("daemons").ValueKind.ShouldBe(JsonValueKind.Array);
        entry.GetProperty("stale").ValueKind.ShouldBe(JsonValueKind.False);
    }

    private SupervisionState NewState() => new();

    private static JsonElement Row(JsonElement report, string storeKey) => report.GetProperty("stores")
        .EnumerateArray()
        .Single(store => store.GetProperty("storeKey").GetString() == storeKey);

    private static void WriteRawState(string json)
    {
        Directory.CreateDirectory(WatchdPaths.Dir);
        File.WriteAllText(WatchdPaths.StatePath, json);
    }

    // The lock is the only test a reader has for "a supervisor is alive here": WatchdLock answers refused to
    // the second claimant, so holding it in-process stands in for a running watchd without spawning one.
    private static WatchdLock HoldWatchdLock()
    {
        var claim = new WatchdLock();
        claim.TryAcquire().Acquired.ShouldBeTrue("test failed to hold the watchd lock the scenario needs");
        return claim;
    }

    private async Task<(string Root, string StoreKey)> NewWorkspaceAsync(string name)
    {
        var root = TestDirs.NewTempDir();
        var init = await CliTestProcess.RunWithHomeAsync(_home, root, "init", "--name", name, "--json");
        init.ExitCode.ShouldBe(0);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(root);
        return (root, WorkspaceRegistry.FindByRootPath(root)!.StoreKey);
    }

    private SupervisorLoop NewLoop(RecordingLauncher launcher, WorkspaceRegistryEntry entry) =>
        new(
            launcher,
            NewState(),
            new CrashLoopGuard(),
            () => [entry],
            storeDir => WatchStoreHealth.Probe(storeDir, Now, _ => false),
            _ => Now,
            () => Now,
            SupervisorLoop.PollInterval);

    private static void WriteHeartbeat(string storeDir, string kind, int pid, DateTime startedAt, DateTime lastBeatAt)
    {
        var json = JsonSerializer.Serialize(new DaemonHeartbeat(pid, kind, startedAt, lastBeatAt), SqueezrJson.FileOptions);
        File.WriteAllText(DaemonHeartbeat.PathFor(storeDir, kind), json);
    }

    private Process StartFakeSqueezrDaemon() => StartProcess("/bin/sh", CreateFakeSqueezrBinary(), "daemon");

    private static string CreateFakeSqueezrBinary()
    {
        var dir = TestDirs.NewTempDir();
        var path = Path.Combine(dir, "squeezr");
        File.WriteAllText(path, "#!/bin/sh\nwhile :; do sleep 1; done\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return path;
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

    private static List<JsonElement> JsonLines(string stdout) => stdout
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        })
        .ToList();

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
            var app = injection is { } registered
                ? new CommandApp(new SingleInstanceRegistrar(registered))
                : new CommandApp();
            app.Configure(config =>
            {
                config.AddCommand<FleetCommand>("fleet");
                config.AddCommand<FleetStartCommand>("start");
                config.AddCommand<FleetStopCommand>("stop");
                config.AddCommand<FleetRestartCommand>("restart");
                config.AddCommand<FleetStatusCommand>("status");
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

    private sealed class SingleInstanceRegistrar((Type Service, object Instance) registration) : ITypeRegistrar
    {
        public void Register(Type service, Type implementation)
        {
        }

        public void RegisterInstance(Type service, object implementation)
        {
            if (service == registration.Service)
            {
                registration = (service, implementation);
            }
        }

        public void RegisterLazy(Type service, Func<object> factory)
        {
        }

        public ITypeResolver Build() => new Resolver(registration);

        private sealed class Resolver((Type Service, object Instance) registration) : ITypeResolver
        {
            public object? Resolve(Type? type)
            {
                if (type is null)
                {
                    return null;
                }

                if (type == registration.Service)
                {
                    return registration.Instance;
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

    private sealed class RecordingLauncher : IWatchDaemonLauncher
    {
        public List<string> Roots { get; } = [];

        public IWatchDaemonHandle StartWatch(string rootPath)
        {
            Roots.Add(rootPath);
            return new FakeHandle();
        }
    }

    private sealed class FakeHandle : IWatchDaemonHandle
    {
        public int Id => 9999;

        public string BinaryPath => "squeezr-fake-binary";

        public DateTime? StartedUtc => null;

        public bool HasExited => false;

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
