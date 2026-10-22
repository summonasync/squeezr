using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

// What T3 owes an operator and an agent: a serve that skipped the watch says who owns the store, in the human
// line, on the machine channel, and in the first answer the agent gets back. Every case reads what the process
// actually wrote, so a verdict split that only exists in the source cannot pass.
public sealed class ServeWatchDegradationTests
{
    private static readonly TimeSpan EngineArmTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StartupLineTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Observer_NamesTheOwnerAcrossTextMachineAndAgentNotice()
    {
        var workspace = NewIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        HoldClaim(storeDir);
        var stderr = new LineCapture();

        await using var harness = await McpTestHarness.StartAsync(
            workspace, watch: true, claimOverride: ClaimAs(storeDir, ForeignPid()), stderr: stderr);

        // the claim held here is this process's, so the observer has to name exactly this pid
        var ownerPid = Environment.ProcessId;
        var human = await stderr.AwaitFor($"pid {ownerPid} owns this store", StartupLineTimeout);
        human.ShouldBe(
            $"warning: watch not started: pid {ownerPid} owns this store; serving queries + per-query freshness");

        var machine = JsonDocument.Parse(stderr.MachineLines.ShouldHaveSingleItem()).RootElement;
        machine.GetProperty("role").GetString().ShouldBe("observer");
        machine.GetProperty("verdict").GetString().ShouldBe("OwnerHold");
        machine.GetProperty("ownerPid").GetInt32().ShouldBe(ownerPid);

        var warnings = await WarningsAsync(harness);
        warnings.ShouldContain(w => w.Contains($"pid {ownerPid}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnerStartup_KeepsItsHumanLines_AndDeclaresItselfOnTheMachineChannel()
    {
        var workspace = NewIndexedWorkspace();
        var stderr = new LineCapture();
        var harness = await McpTestHarness.StartAsync(
            workspace, watch: true, embedWatch: false, stderr: stderr);

        await WaitForEngineArmedAsync(workspace);
        await stderr.AwaitFor("\"role\":\"owner\"", StartupLineTimeout);

        stderr.HumanLines.ShouldBe([
            "squeezr serve: watch embed drain off (--no-embed-watch)",
            "squeezr serve: watch engine running"]);
        var machine = JsonDocument.Parse(stderr.MachineLines.ShouldHaveSingleItem()).RootElement;
        machine.GetProperty("role").GetString().ShouldBe("owner");
        machine.GetProperty("verdict").GetString().ShouldBe("Acquired");
        machine.GetProperty("ownerPid").GetInt32().ShouldBe(Environment.ProcessId);

        await harness.DisposeAsync();

        // a clean stop releases the marker; the release stays silent, so a successful owner run ends where it began
        stderr.HumanLines.ShouldBe([
            "squeezr serve: watch embed drain off (--no-embed-watch)",
            "squeezr serve: watch engine running"]);
        stderr.MachineLines.Length.ShouldBe(1);
    }

    [Fact]
    public async Task ObserverMachineLine_CarriesTypedFieldsAndNoProse()
    {
        var workspace = NewIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        HoldClaim(storeDir);
        var stderr = new LineCapture();

        await using var _ = await McpTestHarness.StartAsync(
            workspace, watch: true, claimOverride: ClaimAs(storeDir, ForeignPid()), stderr: stderr);

        var line = await stderr.AwaitFor("\"type\":\"watchRole\"", StartupLineTimeout);
        using var parsed = JsonDocument.Parse(line);
        var machine = parsed.RootElement;
        machine.GetProperty("role").ValueKind.ShouldBe(JsonValueKind.String);
        machine.GetProperty("verdict").ValueKind.ShouldBe(JsonValueKind.String);
        machine.GetProperty("ownerPid").ValueKind.ShouldBe(JsonValueKind.Number);
        machine.EnumerateObject().Select(p => p.Name).ShouldBe(["type", "role", "verdict", "ownerPid"]);
    }

    [Theory]
    // StoreUnavailable and RaceLost never reach a marker and an unreadable one is too fresh to reap: all three
    // have no owner to blame, so their line has to say the real reason and stay pid-free.
    [InlineData(VerdictFixture.StoreUnavailable)]
    [InlineData(VerdictFixture.MarkerUnreadable)]
    [InlineData(VerdictFixture.RaceLost)]
    [InlineData(VerdictFixture.OwnerWedged)]
    public async Task ClaimVerdicts_PrintTheirOwnReason(VerdictFixture fixture)
    {
        var workspace = NewIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var stderr = new LineCapture();

        await using var harness = await McpTestHarness.StartAsync(
            workspace, watch: true, claimOverride: BuildClaim(storeDir, fixture), stderr: stderr);

        var expected = fixture switch
        {
            VerdictFixture.StoreUnavailable => ("StoreUnavailable", "the store directory could not be written", false),
            VerdictFixture.MarkerUnreadable => ("MarkerUnreadable", "the owner marker is unreadable and not yet reclaimable", false),
            VerdictFixture.RaceLost => ("RaceLost", "another process won the claim race for this store", false),
            _ => ("OwnerWedged", "with a stale beat under a live process", true)
        };
        var human = await stderr.AwaitFor(expected.Item2, StartupLineTimeout);
        var machine = JsonDocument.Parse(stderr.MachineLines.ShouldHaveSingleItem()).RootElement;

        machine.GetProperty("verdict").GetString().ShouldBe(expected.Item1);
        human.ShouldStartWith("warning: watch not started: ");
        human.ShouldEndWith("; serving queries + per-query freshness");
        if (expected.Item3)
        {
            machine.GetProperty("ownerPid").GetInt32().ShouldBe(StaleOwnerPid());
        }
        else
        {
            machine.TryGetProperty("ownerPid", out _).ShouldBeFalse($"{expected.Item1} carries no marker to name");
            human.ShouldNotContain("pid");
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task FailedRelease_SaysSoOnTheHumanChannel_AndSaysItOnce()
    {
        var workspace = NewIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var stderr = new LineCapture();
        var harness = await McpTestHarness.StartAsync(
            workspace, watch: true, embedWatch: false, stderr: stderr);
        await WaitForEngineArmedAsync(workspace);

        // unlink is an operation on the directory, so a store that cannot be written makes the graceful release
        // fail for real — the shape that used to leave a live pid in the marker with no signal at all
        File.SetUnixFileMode(storeDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await harness.DisposeAsync();
        }
        finally
        {
            File.SetUnixFileMode(storeDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        stderr.HumanLines.ShouldContain(
            "warning: serve watch: owner marker could not be released",
            "a failed graceful release has to be readable by whoever is staring at stderr");
        stderr.MachineLines.Length.ShouldBe(1,
            "the release fault is prose, so it stays off the machine channel");
    }

    public enum VerdictFixture
    {
        StoreUnavailable,
        MarkerUnreadable,
        RaceLost,
        OwnerWedged
    }

    private static WatchOwnerClaim BuildClaim(string storeDir, VerdictFixture fixture) => fixture switch
    {
        VerdictFixture.StoreUnavailable => new WatchOwnerClaim(
            Path.Combine(storeDir, "gone"), DaemonHeartbeat.ServeWatchKind, null, null, null, ForeignPid()),
        VerdictFixture.MarkerUnreadable => UnreadableMarkerClaim(storeDir),
        VerdictFixture.RaceLost => new WatchOwnerClaim(
            storeDir, DaemonHeartbeat.ServeWatchKind, null, null, null, ForeignPid(), _ => false),
        _ => WedgedOwnerClaim(storeDir)
    };

    private static WatchOwnerClaim UnreadableMarkerClaim(string storeDir)
    {
        File.WriteAllText(WatchOwnerMarker.PathFor(storeDir), "{ this is not a marker");
        return new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind, null, null, null, ForeignPid());
    }

    // A beat past the stale window under a pid the probe answers to is the wedged owner: it holds the store
    // without indexing it, and it is the one blocked verdict whose marker carries a pid worth naming.
    private static WatchOwnerClaim WedgedOwnerClaim(string storeDir)
    {
        var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        var marker = new WatchOwnerMarker(
            StaleOwnerPid(), Environment.MachineName, DaemonHeartbeat.ServeWatchKind, now, now);
        File.WriteAllText(
            WatchOwnerMarker.PathFor(storeDir), JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));
        return new WatchOwnerClaim(
            storeDir, DaemonHeartbeat.ServeWatchKind,
            () => now + TimeSpan.FromSeconds(60), _ => true, null, ForeignPid());
    }

    // the seam clock is the claim's only liveness input, so any positive pid works while the probe says alive
    private static int StaleOwnerPid() => 4242;

    private static WatchOwnerClaim ClaimAs(string storeDir, int pid) =>
        new(storeDir, DaemonHeartbeat.ServeWatchKind, null, null, null, pid);

    private static void HoldClaim(string storeDir) =>
        new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind).TryAcquire().IsOwner
            .ShouldBeTrue("test failed to hold the claim that arranges the observer scenario");

    private static int ForeignPid() => Environment.ProcessId ^ 0x3FFF;

    private static async Task WaitForEngineArmedAsync(string workspace)
    {
        var deadline = DateTime.UtcNow + EngineArmTimeout;
        while (DateTime.UtcNow < deadline && DaemonHeartbeatReader.TryRead(
                   SqueezrPaths.StoreDir(workspace), DaemonHeartbeat.ServeWatchKind)?.Pid != Environment.ProcessId)
        {
            await Task.Delay(50);
        }

        DaemonHeartbeatReader.TryRead(
            SqueezrPaths.StoreDir(workspace), DaemonHeartbeat.ServeWatchKind)?.Pid
            .ShouldBe(Environment.ProcessId, $"watch engine did not arm within {EngineArmTimeout.TotalSeconds}s");
    }

    private static async Task<IReadOnlyList<string>> WarningsAsync(McpTestHarness harness)
    {
        var result = await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        result.IsError.ShouldNotBe(
            true, $"index_status failed: {string.Join(" ", result.Content.Select(b => b.ToString()))}");
        return JsonDocument.Parse(result.Content.ShouldHaveSingleItem().ToString()!)
            .RootElement.GetProperty("warnings")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        manager.AddRepo("repo", "demo");
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { Enabled = false, DebounceSeconds = 1 }
        });
        File.WriteAllText(Path.Combine(workspace, "repo", "a.cs"), "class Alpha { string Term = \"uniqneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    // stderr arrives through the host's test seam; the writer is shared between the serve task and the assert
    // thread, so both sides take the same lock instead of racing on StringWriter's internals.
    private sealed class LineCapture : StringWriter
    {
        private readonly object _sync = new();

        public override void Write(string? value)
        {
            lock (_sync)
            {
                base.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                base.WriteLine(value);
            }
        }

        public override void Flush()
        {
            lock (_sync)
            {
                base.Flush();
            }
        }

        public string[] HumanLines => Lines().Where(l => !l.StartsWith('{')).ToArray();

        public string[] MachineLines => Lines().Where(l => l.StartsWith('{')).ToArray();

        public async Task<string> AwaitFor(string needle, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var hit = Lines().FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal));
                if (hit is not null)
                {
                    return hit;
                }

                await Task.Delay(50);
            }

            throw new InvalidOperationException(
                $"stderr never said '{needle}' within {timeout.TotalSeconds}s; it said:{Environment.NewLine}{string.Join(Environment.NewLine, Lines())}");
        }

        private string[] Lines()
        {
            lock (_sync)
            {
                return ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
    }
}
