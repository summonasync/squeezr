using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Cli;

public sealed class FleetActivityTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _storeDir = TestDirs.NewTempDir();

    [Fact]
    public void LiveBeat_CarryingActivity_FillsTheRosterRow()
    {
        WriteBeat(4242, activity: new DaemonActivity(
            "files", "alpha", 12, 40, "src/A.cs", Now.AddSeconds(-10)));

        var row = ProbeLive(4242);

        row.Phase.ShouldBe("files");
        row.Repo.ShouldBe("alpha");
        row.DoneItems.ShouldBe(12);
        row.TotalItems.ShouldBe(40);
        row.PhaseSeconds.ShouldBe(10);
        row.Provider.ShouldBeNull();
        row.Executor.ShouldBeNull();
    }

    [Fact]
    public void EmbeddingsBeat_CarriesProviderAndExecutor_AsTheBeatPublishedThem()
    {
        WriteBeat(4242, activity: new DaemonActivity(
            "embeddings", "alpha", 3, 9, null, Now.AddSeconds(-30), "gpu", "ollama"));

        var row = ProbeLive(4242);

        row.Provider.ShouldBe("ollama");
        row.Executor.ShouldBe("gpu");
    }

    [Fact]
    public void DaemonBetweenPasses_AnswersIdle_AndKeepsItsJsonContractFieldsPresent()
    {
        WriteBeat(4242);

        var row = ProbeLive(4242);

        row.Phase.ShouldBeNull();
        row.Repo.ShouldBeNull();
        row.DoneItems.ShouldBeNull();
        row.TotalItems.ShouldBeNull();
        row.PhaseSeconds.ShouldBeNull();
        row.Provider.ShouldBeNull();
        row.Executor.ShouldBeNull();

        var json = JsonSerializer.Serialize(row, SqueezrJson.OutputOptions);
        var element = JsonDocument.Parse(json).RootElement;
        element.GetProperty("phase").ValueKind.ShouldBe(JsonValueKind.Null);
        element.GetProperty("rssBytes").ValueKind.ShouldBe(JsonValueKind.Null);
        element.EnumerateObject().Select(property => property.Name)
            .OrderBy(key => key, StringComparer.Ordinal).ShouldBe([
                "cpuPercent", "doneItems", "executor", "host", "isAlive", "kind", "owner", "phase",
                "phaseSeconds", "pid", "provider", "repo", "rssBytes", "status", "totalItems", "uptimeSeconds"]);
    }

    [Fact]
    public void StaleBeat_CarryingActivity_NeverRendersItAsLiveWork()
    {
        WriteBeat(
            4242,
            beatAge: TimeSpan.FromMinutes(5),
            activity: new DaemonActivity("files", "alpha", 12, 40, null, Now.AddMinutes(-5)));

        var row = Probe(4242, pid => true);

        row.IsAlive.ShouldBeFalse();
        row.Phase.ShouldBeNull();
        row.Repo.ShouldBeNull();
        row.DoneItems.ShouldBeNull();
        row.TotalItems.ShouldBeNull();
        row.PhaseSeconds.ShouldBeNull();
        row.Executor.ShouldBeNull();
    }

    [Fact]
    public void GonePid_WithAFreshBeat_CarryingActivity_NeverRendersItAsLiveWork()
    {
        WriteBeat(4242, activity: new DaemonActivity(
            "embeddings", "alpha", 3, 9, null, Now.AddSeconds(-2), "gpu", "ollama"));

        var row = Probe(4242, pid => false);

        row.IsAlive.ShouldBeFalse();
        row.Phase.ShouldBeNull();
        row.Executor.ShouldBeNull();
        row.CpuPercent.ShouldBeNull();
        row.RssBytes.ShouldBeNull();
    }

    [Fact]
    public void ActivityCell_RendersTheWorkShapesTheTaskContractNames()
    {
        FleetCommand.FormatActivity([Row("files", "alpha", 12, 40)]).ShouldBe("indexing alpha · files 12/40");
        FleetCommand.FormatActivity([Row("embeddings", "alpha", 3, 9, "gpu", "ollama")])
            .ShouldBe("embedding alpha · chunks 3/9 · ollama · gpu");
        FleetCommand.FormatActivity([Row("embeddings", "alpha", 3, 9, "cpu", "openai")])
            .ShouldBe("embedding alpha · chunks 3/9 · openai · cpu");
        FleetCommand.FormatActivity([Row("walk", "alpha", 0, 0)]).ShouldBe("walking alpha");
        FleetCommand.FormatActivity([Row("semantic", "alpha", 5, 20)]).ShouldBe("semantic alpha · 5/20");
        FleetCommand.FormatActivity([Row("trigram", "alpha", 5, 20)]).ShouldBe("trigram alpha · 5/20");
        FleetCommand.FormatActivity([Row("future[phase]", "al[pha]", 1, 2)])
            .ShouldBe("future[[phase]] al[[pha]] · 1/2");
    }

    [Fact]
    public void ActivityCell_LiveDaemonBetweenPasses_AnswersIdle_NeverAnEmptyCell()
    {
        FleetCommand.FormatActivity([Idle(4242)]).ShouldBe("idle");
    }

    [Fact]
    public void ActivityCell_NothingAlive_AnswersIdleQuestion_AndEmptyRosterKeepsTheDash()
    {
        FleetCommand.FormatActivity([Dead(4242)]).ShouldBe("idle?");
        FleetCommand.FormatActivity([]).ShouldBe("—");
    }

    [Fact]
    public void ActivityCell_MultiPidStore_RendersOneLinePerLiveProcess()
    {
        var cell = FleetCommand.FormatActivity([
            Idle(4343),
            Row("files", "alpha", 12, 40),
            Dead(4444),
        ]);

        cell.ShouldBe($"idle{Environment.NewLine}indexing alpha · files 12/40");
    }

    [Fact]
    public void SampleOnce_SamplesEveryLivePidOfEveryStore_InOneSharedWindow()
    {
        var calls = 0;
        IReadOnlyCollection<int> sampled = [];
        var window = TimeSpan.Zero;
        var rosters = new IReadOnlyList<FleetDaemonProcess>[]
        {
            [Idle(4242), Row("files", "alpha", 1, 2, pid: 4343), Dead(4545)],
            [Idle(4646), Dead(4242)],
        };

        FleetListing.SampleOnce(rosters, (pids, span) =>
        {
            calls++;
            sampled = pids;
            window = span;
            return new Dictionary<int, DaemonResourceSample> { [4242] = new(50, 1 << 20) };
        });

        calls.ShouldBe(1);
        window.ShouldBe(DaemonResourceSampler.Window);
        sampled.ShouldBe([4242, 4343, 4646], ignoreOrder: true);
    }

    [Fact]
    public void SampleOnce_WithNothingAlive_NeverCallsTheSampler()
    {
        var calls = 0;

        var samples = FleetListing.SampleOnce([[Dead(4242)]], (_, _) =>
        {
            calls++;
            return new Dictionary<int, DaemonResourceSample>();
        });

        calls.ShouldBe(0);
        samples.ShouldBeEmpty();
    }

    [Fact]
    public void StampResources_FillsLiveRows_AndLeavesDeadRowsUnmeasured()
    {
        var samples = new Dictionary<int, DaemonResourceSample>
        {
            [4242] = new(87.5, 1 << 20),
            [4343] = new(null, null),
        };

        var fed = FleetListing.StampResources(Row("files", "alpha", 1, 2, pid: 4242), samples);
        var unmeasured = FleetListing.StampResources(Idle(4343), samples);
        var dead = FleetListing.StampResources(Dead(4545), samples);

        fed.CpuPercent.ShouldBe(87.5);
        fed.RssBytes.ShouldBe(1 << 20);
        unmeasured.CpuPercent.ShouldBeNull();
        unmeasured.RssBytes.ShouldBeNull();
        dead.CpuPercent.ShouldBeNull();
        dead.RssBytes.ShouldBeNull();
    }

    private FleetDaemonProcess ProbeLive(int pid) => Probe(pid, pids => true);

    private FleetDaemonProcess Probe(int pid, Func<int, bool> isProcessAlive) =>
        FleetListing.ProbeDaemons(_storeDir, Now, isProcessAlive).Single(row => row.Pid == pid);

    private static FleetDaemonProcess Row(
        string phase,
        string repo,
        int done,
        int total,
        string? executor = null,
        string? provider = null,
        int pid = 4242) =>
        new(DaemonHeartbeat.DefaultKind, pid, FleetDaemonState.AliveStatus, 60, false, null)
        {
            Phase = phase,
            Repo = repo,
            DoneItems = done,
            TotalItems = total,
            PhaseSeconds = 5,
            Provider = provider,
            Executor = executor,
        };

    private static FleetDaemonProcess Idle(int pid) =>
        new(DaemonHeartbeat.DefaultKind, pid, FleetDaemonState.AliveStatus, 60, false, null);

    private static FleetDaemonProcess Dead(int pid) =>
        new(DaemonHeartbeat.DefaultKind, pid, FleetDaemonState.DeadStatus, null, false, null);

    private void WriteBeat(int pid, TimeSpan? beatAge = null, DaemonActivity? activity = null)
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
        var now = beatAge is null ? Now : Now - beatAge.Value;
        var beat = new DaemonHeartbeat(
            pid, DaemonHeartbeat.DefaultKind, now - TimeSpan.FromMinutes(3), now, "box", activity);
        File.WriteAllText(
            DaemonHeartbeat.BeatPathFor(_storeDir, DaemonHeartbeat.DefaultKind, pid),
            JsonSerializer.Serialize(beat, SqueezrJson.FileOptions));
    }

    public void Dispose()
    {
        Directory.Delete(_storeDir, recursive: true);
    }
}
