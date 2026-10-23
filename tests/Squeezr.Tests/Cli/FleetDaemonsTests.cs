using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Squeezr.Cli.Commands;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class FleetDaemonsTests : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);
    private readonly List<Process> _children = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var child in _children)
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
            }

            child.Dispose();
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Json_TwoLiveBeats_ListsBoth_MarksExactlyTheClaimOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-roster-two");
        var storeDir = SqueezrPaths.StoreDir(await NewWorkspaceAsync(name));
        var secondPid = StartLiveProcess();
        WriteBeat(storeDir, DaemonHeartbeat.ServeWatchKind, Environment.ProcessId);
        WriteBeat(storeDir, DaemonHeartbeat.ServeWatchKind, secondPid);
        WriteOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind, Environment.ProcessId);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        var daemons = SingleEntry(result, name).GetProperty("daemons");
        daemons.EnumerateArray().Count().ShouldBe(2);
        var rows = daemons.EnumerateArray()
            .ToDictionary(row => row.GetProperty("pid").GetInt32(), row => row);
        rows[Environment.ProcessId].GetProperty("owner").GetBoolean().ShouldBeTrue();
        rows[secondPid].GetProperty("owner").GetBoolean().ShouldBeFalse();
        rows.Values.ShouldAllBe(row => row.GetProperty("status").GetString() == "alive");
        rows.Values.ShouldAllBe(row => row.GetProperty("kind").GetString() == DaemonHeartbeat.ServeWatchKind);
    }

    [Fact]
    public async Task Json_OldSingleFileBeat_StillAnswersAsOneProcess()
    {
        var name = UniqueName("fleet-roster-legacy");
        var storeDir = SqueezrPaths.StoreDir(await NewWorkspaceAsync(name));
        WriteKindHeartbeat(storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        result.ExitCode.ShouldBe(0);
        var entry = SingleEntry(result, name);
        entry.GetProperty("watch").GetProperty("status").GetString().ShouldBe("alive");
        entry.GetProperty("watch").GetProperty("pid").GetInt32().ShouldBe(Environment.ProcessId);
        entry.GetProperty("daemons").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("pid").GetInt32().ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public async Task DeadBeatPastTheStaleWindow_IsListedButNeverCalledAlive()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-roster-dead");
        var storeDir = SqueezrPaths.StoreDir(await NewWorkspaceAsync(name));
        var gonePid = StartLiveProcess();
        WriteBeat(storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId);
        WriteBeat(storeDir, DaemonHeartbeat.DefaultKind, gonePid, beatAge: TimeSpan.FromMinutes(5));
        ExitProcess(gonePid);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        var daemons = SingleEntry(result, name).GetProperty("daemons").EnumerateArray()
            .ToDictionary(row => row.GetProperty("pid").GetInt32(), row => row);
        daemons[gonePid].GetProperty("status").GetString().ShouldBe("dead");
        daemons[gonePid].GetProperty("owner").GetBoolean().ShouldBeFalse();
        daemons[Environment.ProcessId].GetProperty("status").GetString().ShouldBe("alive");

        var human = await RunInProcessFleetAsync();
        human.ShouldContain($"dead (pid {gonePid})");
    }

    [Fact]
    public async Task ClaimOwnedFromAnotherHost_NamesNoOwnerHere_BecauseThatPidIsNotOursToProbe()
    {
        var name = UniqueName("fleet-roster-foreign-claim");
        var storeDir = SqueezrPaths.StoreDir(await NewWorkspaceAsync(name));
        WriteBeat(storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId);
        WriteOwnerClaim(storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId, host: "elsewhere");

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        SingleEntry(result, name).GetProperty("daemons").EnumerateArray().ShouldHaveSingleItem()
            .GetProperty("owner").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Human_TableListsEveryPidOfTheKindAndMarksTheOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-roster-human");
        var root = await NewWorkspaceAsync(name);
        var storeDir = SqueezrPaths.StoreDir(root);
        var observerPid = StartLiveProcess();
        WriteBeat(storeDir, DaemonHeartbeat.ServeWatchKind, Environment.ProcessId);
        WriteBeat(storeDir, DaemonHeartbeat.ServeWatchKind, observerPid);
        WriteOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind, Environment.ProcessId);

        var output = await RunInProcessFleetAsync();

        output.ShouldContain($"pid {Environment.ProcessId}, up");
        output.ShouldContain("(owner)");
        output.ShouldContain("(observer)");
        output.ShouldContain($"pid {observerPid}");
    }

    [Fact]
    public async Task KindHeartbeatKeys_KeepTheirPerKindMeaning_WhileTheRosterCarriesTheObservers()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var name = UniqueName("fleet-roster-kinds");
        var storeDir = SqueezrPaths.StoreDir(await NewWorkspaceAsync(name));
        var observerPid = StartLiveProcess();
        WriteBeat(storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId);
        WriteKindHeartbeat(storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId);
        WriteBeat(storeDir, DaemonHeartbeat.ServeWatchKind, observerPid);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        var entry = SingleEntry(result, name);
        entry.GetProperty("watch").GetProperty("pid").GetInt32().ShouldBe(Environment.ProcessId);
        entry.GetProperty("watch").GetProperty("status").GetString().ShouldBe("alive");
        // The observer publishes a beat and no per-kind heartbeat: it indexes nothing, so the heartbeat key it
        // never wrote stays `none` while the roster names it. Reading only the old keys loses nothing.
        entry.GetProperty("serveWatch").GetProperty("status").GetString().ShouldBe("none");
        entry.GetProperty("daemons").EnumerateArray().Count().ShouldBe(2);
    }

    // The declared --json shape is a contract (035): a member that arrives here uninvited is a silent break for a
    // consumer written against the published list, and no other test in this file would notice it. isAlive rides
    // along because FleetDaemonState has carried the same computed member since before 070 — symmetry, not an accident.
    [Fact]
    public async Task Json_DaemonRowCarriesExactlyTheDeclaredKeys()
    {
        var name = UniqueName("fleet-roster-keys");
        var storeDir = SqueezrPaths.StoreDir(await NewWorkspaceAsync(name));
        WriteBeat(storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId);

        var result = await RunCliAsync(TestDirs.NewTempDir(), "fleet", "--json");

        var keys = SingleEntry(result, name).GetProperty("daemons").EnumerateArray().ShouldHaveSingleItem()
            .EnumerateObject().Select(property => property.Name)
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        keys.ShouldBe(["host", "isAlive", "kind", "owner", "pid", "status", "uptimeSeconds"]);
    }

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<string> NewWorkspaceAsync(string name)
    {
        var root = TestDirs.NewTempDir();
        var init = await RunCliAsync(root, "init", "--name", name, "--json");
        init.ExitCode.ShouldBe(0);
        return root;
    }

    private static void WriteBeat(
        string storeDir, string kind, int pid, TimeSpan? beatAge = null, string? host = null)
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(storeDir));
        var now = DateTime.UtcNow;
        var startedAt = now - TimeSpan.FromMinutes(3);
        var beat = new DaemonHeartbeat(
            pid, kind, startedAt, beatAge is null ? now : now - beatAge.Value, host ?? Environment.MachineName);
        File.WriteAllText(
            DaemonHeartbeat.BeatPathFor(storeDir, kind, pid),
            JsonSerializer.Serialize(beat, SqueezrJson.FileOptions));
    }

    private static void WriteKindHeartbeat(string storeDir, string kind, int pid, TimeSpan? beatAge = null)
    {
        var now = DateTime.UtcNow;
        var beat = new DaemonHeartbeat(
            pid, kind, now - TimeSpan.FromMinutes(3), beatAge is null ? now : now - beatAge.Value);
        File.WriteAllText(
            DaemonHeartbeat.PathFor(storeDir, kind),
            JsonSerializer.Serialize(beat, SqueezrJson.FileOptions));
    }

    private static void WriteOwnerClaim(string storeDir, string kind, int pid, string? host = null)
    {
        var now = DateTime.UtcNow;
        var marker = new WatchOwnerMarker(pid, host ?? Environment.MachineName, kind, now, now);
        File.WriteAllText(
            WatchOwnerMarker.PathFor(storeDir), JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));
    }

    // A beat only counts as a live process while the pid exists, so the roster test needs a second real one.
    private int StartLiveProcess()
    {
        var startInfo = new ProcessStartInfo("/bin/sleep", "300") { UseShellExecute = false };
        var process = Process.Start(startInfo)!;
        _children.Add(process);
        return process.Id;
    }

    private void ExitProcess(int pid)
    {
        var process = _children.Single(candidate => candidate.Id == pid);
        process.Kill(entireProcessTree: true);
        process.WaitForExit(WaitTimeout.Milliseconds);
    }

    private static JsonElement SingleEntry((int ExitCode, string StdOut, string StdErr) result, string name) =>
        result.StdOut.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Single(entry => entry.GetProperty("name").GetString() == name);

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);

    // The in-process table renders every workspace of the run's home, and other suites keep registering rows into
    // it while this class runs: the name column is not a stable denominator here. The assertion is about the
    // daemon rows this store owns, and pids are what the slice renders.
    private static async Task<string> RunInProcessFleetAsync()
    {
        var previousConsole = AnsiConsole.Console;
        var console = new TestConsole();
        console.Profile.Width = 400;
        AnsiConsole.Console = console;
        try
        {
            var app = new CommandApp();
            app.Configure(config => config.AddCommand<FleetCommand>("fleet"));
            (await app.RunAsync(["fleet"])).ShouldBe(0);
            return console.Output;
        }
        finally
        {
            AnsiConsole.Console = previousConsole;
        }
    }
}
