using System.Text.Json;
using Shouldly;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Indexing.Watch;

public sealed class DaemonHeartbeatActivityTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan SilentInterval = TimeSpan.FromMinutes(30);
    private readonly string _storeDir = TestDirs.NewTempDir();
    private DateTime _now = T0;

    [Fact]
    public void Start_WithoutAnyPass_WritesAnIdleBeatWithNoActivity()
    {
        using var writer = CreateWriter();

        writer.Start();

        var beat = ReadBeat();
        beat.ShouldNotBeNull("a never-started-pass daemon still beats, just idle");
        beat.Activity.ShouldBeNull();
    }

    [Fact]
    public void PhaseStarted_FlushesImmediately_WithoutWaitingForTheBeatInterval()
    {
        using var writer = CreateWriter();
        writer.Start();

        Advance(TimeSpan.FromSeconds(1));
        writer.PhaseStarted("demo", IndexPhase.Walk, 0);

        var activity = ReadBeat().ShouldNotBeNull().Activity.ShouldNotBeNull(
            "a phase change must reach the beat file at once, not on the next interval");
        activity.Phase.ShouldBe("walk");
        activity.Repo.ShouldBe("demo");
        activity.DoneItems.ShouldBe(0);
        activity.TotalItems.ShouldBe(0);
        activity.CurrentItem.ShouldBeNull();
        activity.PhaseStartedAt.ShouldBe(T0 + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ItemProcessed_CountsDoneFromThePhaseStart_AndRidesTheNextBeat()
    {
        using var writer = CreateWriter();
        writer.Start();
        Advance(TimeSpan.FromSeconds(1));
        writer.PhaseStarted("demo", IndexPhase.Files, 3);
        var flushed = ReadBeatText();

        Advance(TimeSpan.FromSeconds(1));
        writer.ItemProcessed("demo", "a.cs");
        Advance(TimeSpan.FromSeconds(1));
        writer.ItemProcessed("demo", "b.cs");

        ReadBeatText().ShouldBe(flushed, "per-item ticks must not rewrite the beat file");

        Advance(TimeSpan.FromSeconds(4));
        writer.Beat();

        var activity = ReadBeat().ShouldNotBeNull().Activity.ShouldNotBeNull();
        activity.Phase.ShouldBe("files");
        activity.DoneItems.ShouldBe(2);
        activity.CurrentItem.ShouldBe("b.cs");
        activity.TotalItems.ShouldBe(3);
        activity.PhaseStartedAt.ShouldBe(T0 + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RepoCompleted_ClearsActivity_AndFlushesTheIdleBeat()
    {
        using var writer = CreateWriter();
        writer.Start();
        writer.PhaseStarted("demo", IndexPhase.Files, 2);

        Advance(TimeSpan.FromSeconds(1));
        writer.RepoCompleted("demo");

        ReadBeat().ShouldNotBeNull().Activity.ShouldBeNull();
    }

    [Fact]
    public void RepoCompleted_ForARepoWhoseActivityWasReplaced_KeepsTheRunningPhaseUntouched()
    {
        using var writer = CreateWriter();
        writer.Start();
        writer.PhaseStarted("alpha", IndexPhase.Files, 2);
        writer.ItemProcessed("alpha", "a.cs");
        writer.PhaseStarted("beta", IndexPhase.Trigram, 4);
        var flushed = ReadBeatText();

        Advance(TimeSpan.FromSeconds(1));
        writer.RepoCompleted("alpha");

        ReadBeatText().ShouldBe(flushed, "a stale repo completion must not clear or rewrite the running phase");
        ReadBeat().ShouldNotBeNull().Activity!.Repo.ShouldBe("beta");

        writer.RepoCompleted("beta");
        ReadBeat().ShouldNotBeNull().Activity.ShouldBeNull();
    }

    [Fact]
    public void PassOverSeveralFiles_RewritesTheBeatOnPhaseChangesOnly_NeverPerItem()
    {
        using var writer = CreateWriter();
        writer.Start();
        var written = new HashSet<string> { ReadBeatText() };

        writer.PhaseStarted("demo", IndexPhase.Walk, 0);
        written.Add(ReadBeatText());
        writer.PhaseStarted("demo", IndexPhase.Files, 5);
        written.Add(ReadBeatText());
        for (var file = 0; file < 5; file++)
        {
            Advance(TimeSpan.FromMilliseconds(250));
            writer.ItemProcessed("demo", $"file{file}.cs");
            written.Add(ReadBeatText());
        }

        writer.PhaseStarted("demo", IndexPhase.Trigram, 5);
        written.Add(ReadBeatText());
        for (var file = 0; file < 5; file++)
        {
            Advance(TimeSpan.FromMilliseconds(250));
            writer.ItemProcessed("demo", $"file{file}.cs");
            written.Add(ReadBeatText());
        }

        Advance(TimeSpan.FromSeconds(1));
        writer.RepoCompleted("demo");
        written.Add(ReadBeatText());

        written.Count.ShouldBe(5, "a five-file pass must rewrite the beat only on the phase boundaries " +
            "and the completion, never once per item — the pass holds the store lock while it runs");
    }

    [Fact]
    public void SetEmbeddingExecutor_EmbeddingsPhase_CarriesExecutorAndProvider()
    {
        using var writer = CreateWriter();
        writer.Start();
        writer.SetEmbeddingExecutor(new EmbeddingExecutor("gpu", null), "ollama");

        writer.PhaseStarted("demo", IndexPhase.Embeddings, 9);

        var activity = ReadBeat().ShouldNotBeNull().Activity.ShouldNotBeNull();
        activity.Phase.ShouldBe("embeddings");
        activity.Executor.ShouldBe("gpu");
        activity.Provider.ShouldBe("ollama");
    }

    [Fact]
    public void SetEmbeddingExecutor_NonEmbeddingPhase_LeavesTheExecutorOutOfTheBeat()
    {
        using var writer = CreateWriter();
        writer.Start();
        writer.SetEmbeddingExecutor(new EmbeddingExecutor("cpu", null), "ollama");

        writer.PhaseStarted("demo", IndexPhase.Files, 5);

        var text = ReadBeatText();
        text.Contains("executor", StringComparison.Ordinal).ShouldBeFalse(
            "only the embeddings phase answers where inference runs, walk and files stay shape-identical to T1");
        ReadBeat().ShouldNotBeNull().Activity.ShouldNotBeNull().Executor.ShouldBeNull();
    }

    [Fact]
    public void OldActivity_ShapeWithoutExecutor_ReadsDefaultsForTheNewFields()
    {
        var legacy = JsonSerializer.Deserialize<DaemonActivity>(
            """{"phase":"embeddings","repo":"demo","doneItems":0,"totalItems":9,"currentItem":null,"phaseStartedAt":"2026-10-25T12:00:00Z"}""",
            SqueezrJson.FileOptions);

        legacy.ShouldNotBeNull();
        legacy.Executor.ShouldBeNull();
        legacy.Provider.ShouldBeNull();
    }

    [Fact]
    public void OldReader_ShapeWithoutActivity_IgnoresTheUnknownMember()
    {
        using var writer = CreateWriter();
        writer.Start();
        ReadBeatText().Contains("activity").ShouldBeFalse("an idle beat keeps the pre-068 wire shape");
        writer.PhaseStarted("demo", IndexPhase.Embeddings, 9);

        var legacy = JsonSerializer.Deserialize<LegacyBeatShape>(
            File.ReadAllText(writer.BeatPath), SqueezrJson.FileOptions);

        legacy.ShouldNotBeNull();
        legacy.Pid.ShouldBe(Environment.ProcessId);
        legacy.Kind.ShouldBe(DaemonHeartbeat.DefaultKind);
        legacy.StartedAt.ShouldBe(T0);
    }

    [Fact]
    public void CurrentReader_OldBeatWithoutActivity_ReadsDefaultsForTheNewFields()
    {
        var kindPath = DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.DefaultKind);
        File.WriteAllText(
            kindPath,
            JsonSerializer.Serialize(
                new LegacyBeatShape(4242, DaemonHeartbeat.DefaultKind, T0, T0, null),
                SqueezrJson.FileOptions));

        var beat = DaemonHeartbeatReader.TryRead(_storeDir);

        beat.ShouldNotBeNull();
        beat.Pid.ShouldBe(4242);
        beat.Activity.ShouldBeNull();
    }

    private void Advance(TimeSpan span) => _now += span;

    private DaemonHeartbeatWriter CreateWriter() =>
        new(_storeDir, DaemonHeartbeat.DefaultKind, () => _now, SilentInterval);

    private DaemonHeartbeat? ReadBeat() =>
        JsonSerializer.Deserialize<DaemonHeartbeat>(ReadBeatText(), SqueezrJson.FileOptions);

    private string ReadBeatText() => File.ReadAllText(beatPath());

    private string beatPath() =>
        DaemonHeartbeat.BeatPathFor(_storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId);

    private sealed record LegacyBeatShape(
        int Pid, string Kind, DateTime StartedAt, DateTime LastBeatAt, string? Host);

    public void Dispose() => Directory.Delete(_storeDir, recursive: true);
}
